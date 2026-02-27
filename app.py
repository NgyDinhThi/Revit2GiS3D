import os
import json
import uuid
import time
import re
import logging
import threading 
from datetime import datetime
from functools import wraps
from flask import Flask, request, jsonify, send_from_directory, url_for
from flask_socketio import SocketIO, join_room, emit
from dotenv import load_dotenv

# Load biến môi trường
load_dotenv()

# --- CẤU HÌNH ---
logging.basicConfig(level=logging.INFO, format='%(asctime)s %(levelname)s: %(message)s')
logger = logging.getLogger(__name__)

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
UPLOAD_FOLDER = os.path.join(BASE_DIR, "uploads")
STATIC_FOLDER = os.path.join(BASE_DIR, "static")
TEMPLATES_FOLDER = os.path.join(BASE_DIR, "templates")

for folder in [UPLOAD_FOLDER, STATIC_FOLDER, TEMPLATES_FOLDER]:
    os.makedirs(folder, exist_ok=True)

app = Flask(__name__, static_folder=STATIC_FOLDER, static_url_path="/static", template_folder=TEMPLATES_FOLDER)
app.config['MAX_CONTENT_LENGTH'] = 1000 * 1024 * 1024  # 1GB Limit
class MutePullLogsFilter(logging.Filter):
    def filter(self, record):
        # Bỏ qua không in ra màn hình nếu log có chứa chữ "commands/pull"
        return 'commands/pull' not in record.getMessage()

# Áp dụng bộ lọc cho hệ thống log mặc định của Flask (werkzeug)
logging.getLogger('werkzeug').addFilter(MutePullLogsFilter())
# --------------------------------------

# Cấu hình API Key
API_KEY = os.getenv("API_KEY", "CHANGE-ME-IN-PRODUCTION")

socketio = SocketIO(app, cors_allowed_origins='*')
_lock = threading.Lock()  # Bây giờ dòng này sẽ chạy OK

# --- HELPER ---
def _safe(s):
    if not s: return "unknown"
    return re.sub(r'[^\w\-]', '_', s)[:100]

def require_api_key(f):
    @wraps(f)
    def decorated(*args, **kwargs):
        key = request.headers.get('X-API-Key')
        if key != API_KEY:
            logger.warning(f"Unauthorized access from {request.remote_addr}")
            return jsonify({"error": "Unauthorized"}), 401
        return f(*args, **kwargs)
    return decorated

def _read_json(path, default):
    if not os.path.exists(path): return default
    try:
        with open(path, "r", encoding="utf-8") as f: return json.load(f)
    except: return default

def _atomic_write_json(path, data):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f: json.dump(data, f, ensure_ascii=False, indent=2)
    os.replace(tmp, path)

# Paths
def _meta(pid): return os.path.join(UPLOAD_FOLDER, f"project_{_safe(pid)}_meta.json")
def _browser(pid): return os.path.join(UPLOAD_FOLDER, f"browser_{_safe(pid)}_latest.json")
def _cmds(pid): return os.path.join(UPLOAD_FOLDER, f"commands_{_safe(pid)}.json")
def _res(pid, cid): return os.path.join(UPLOAD_FOLDER, f"cmdres_{_safe(pid)}_{_safe(cid)}.json")
def _room(pid): return f"proj:{_safe(pid)}"

# --- ROUTES ---

@app.after_request
def add_cors(resp):
    resp.headers["Access-Control-Allow-Origin"] = "*"
    resp.headers["Access-Control-Allow-Headers"] = "Content-Type, X-API-Key"
    resp.headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS"
    return resp

@app.route("/")
def home():
    return jsonify({"status": "running", "mode": "secure", "api_key_hint": "Check C# settings"})

@app.route("/browser")
def browser_page():
    if os.path.exists(os.path.join(STATIC_FOLDER, "browser.html")):
        return send_from_directory(STATIC_FOLDER, "browser.html")
    return "browser.html missing", 404

@app.route("/viewer_glb")
def viewer_glb():
    if os.path.exists(os.path.join(STATIC_FOLDER, "viewer_glb.html")):
        return send_from_directory(STATIC_FOLDER, "viewer_glb.html")
    if os.path.exists(os.path.join(TEMPLATES_FOLDER, "viewer_glb.html")):
        return send_from_directory(TEMPLATES_FOLDER, "viewer_glb.html")
    return "viewer_glb.html missing", 404

# --- UPLOAD API ---
@app.route("/upload", methods=["POST"])
@require_api_key
def upload():
    files = request.files.getlist("file")
    project_id = request.form.get("projectId", "").strip()
    
    saved = []
    for f in files:
        if f and f.filename:
            ext = os.path.splitext(f.filename)[1].lower()
            name = f"model_{uuid.uuid4().hex}{ext}"
            save_path = os.path.join(UPLOAD_FOLDER, name)
            f.save(save_path)
            saved.append(name)
            
            if project_id and ext == ".glb":
                with _lock:
                    meta = _read_json(_meta(project_id), {})
                    meta["latestGlbFile"] = name
                    meta["updatedAt"] = datetime.utcnow().isoformat() + "Z"
                    _atomic_write_json(_meta(project_id), meta)
                logger.info(f"Updated latest GLB for project {project_id}: {name}")

    return jsonify({"ok": True, "uploaded": saved})

@app.route("/api/projects/<pid>/models/latest-glb")
def get_latest_glb(pid):
    meta = _read_json(_meta(pid), {})
    glb = meta.get("latestGlbFile")
    if not glb:
        return jsonify({"error": "No model found. Please 'Publish' from Revit."}), 404
    
    return jsonify({
        "latestGlbFile": glb,
        "url": url_for("serve_uploads", filename=glb, _external=True)
    })

# --- BROWSER INDEX ---
@app.route("/api/projects/<pid>/browser-index", methods=["POST"])
@require_api_key
def push_index(pid):
    data = request.get_json(force=True)
    data.setdefault("projectId", pid)
    with _lock: _atomic_write_json(_browser(pid), data)
    return jsonify({"ok": True})

@app.route("/api/projects/<pid>/browser-index/latest")
def get_index(pid):
    data = _read_json(_browser(pid), None)
    return jsonify(data) if data else ("Not found", 404)

# --- COMMANDS ---
@app.route("/api/projects/<pid>/commands", methods=["POST"])
@require_api_key
def push_cmd(pid):
    cmd = request.get_json(force=True)
    cmd["id"] = str(uuid.uuid4())
    with _lock:
        cmds = _read_json(_cmds(pid), [])
        cmds.append(cmd)
        _atomic_write_json(_cmds(pid), cmds)
    socketio.emit("command", cmd, room=_room(pid))
    return jsonify({"ok": True, "id": cmd["id"]})

@app.route("/api/projects/<pid>/commands/pull")
def pull_cmds(pid):
    return jsonify({"commands": _read_json(_cmds(pid), [])})

@app.route("/api/projects/<pid>/commands/ack", methods=["POST"])
@require_api_key
def ack_cmds(pid):
    body = request.get_json(force=True)
    ack_ids = set(body.get("ids", []))
    with _lock:
        cmds = _read_json(_cmds(pid), [])
        cmds = [c for c in cmds if c.get("id") not in ack_ids]
        _atomic_write_json(_cmds(pid), cmds)
    return jsonify({"ok": True})

# --- RESULTS ---
@app.route("/api/projects/<pid>/command-results", methods=["POST"])
@require_api_key
def post_res(pid):
    data = request.get_json(force=True)
    cid = data.get("id")
    if cid:
        with _lock: _atomic_write_json(_res(pid, cid), data)
        socketio.emit("command_result", data, room=_room(pid))
    return jsonify({"ok": True})

@app.route("/api/projects/<pid>/command-results/<cid>")
def get_res(pid, cid):
    return jsonify(_read_json(_res(pid, cid), {"status": "pending"}))

# --- SNAPSHOTS ---
@app.route("/api/projects/<pid>/snapshots/upload", methods=["POST"])
@require_api_key
def up_snap(pid):
    if "file" not in request.files: return "No file", 400
    f = request.files["file"]
    if f:
        name = f"snap_{uuid.uuid4().hex}.png"
        f.save(os.path.join(UPLOAD_FOLDER, name))
        return jsonify({"ok": True, "url": url_for("serve_uploads", filename=name, _external=True)})
    return "Error", 400

@app.route("/uploads/<path:filename>")
def serve_uploads(filename):
    return send_from_directory(UPLOAD_FOLDER, filename)

@socketio.on("subscribe")
def on_sub(data):
    pid = (data or {}).get('projectId')
    if pid: join_room(_room(pid))

if __name__ == "__main__":
    print(f"--- SERVER STARTED on 5000 ---")
    print(f"API KEY: {API_KEY}") 
    socketio.run(app, host="0.0.0.0", port=5000, debug=True, use_reloader=False)