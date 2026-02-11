import os
import json
import uuid
import time
import threading
import logging
from datetime import datetime
from functools import wraps
from flask import Flask, request, jsonify, send_from_directory, url_for
from flask_socketio import SocketIO, join_room, emit

# --- CẤU HÌNH ---
BASE_DIR = os.path.dirname(os.path.abspath(__file__))
UPLOAD_FOLDER = os.path.join(BASE_DIR, "uploads")
STATIC_FOLDER = os.path.join(BASE_DIR, "static")
TEMPLATES_FOLDER = os.path.join(BASE_DIR, "templates")

os.makedirs(UPLOAD_FOLDER, exist_ok=True)
os.makedirs(STATIC_FOLDER, exist_ok=True)
os.makedirs(TEMPLATES_FOLDER, exist_ok=True)

# Cấu hình API Key (Phải khớp với file C# RemoteCommandHandler.cs)
API_KEY = os.environ.get("API_KEY", "CHANGE-ME-IN-PRODUCTION")

# Setup Logging
logging.basicConfig(level=logging.INFO, format='%(asctime)s %(levelname)s: %(message)s')
logger = logging.getLogger(__name__)

app = Flask(__name__, static_folder=STATIC_FOLDER, static_url_path="/static", template_folder=TEMPLATES_FOLDER)
app.config['MAX_CONTENT_LENGTH'] = 500 * 1024 * 1024  # 500MB Limit

socketio = SocketIO(app, cors_allowed_origins='*')
_lock = threading.Lock()

# --- HELPER: BẢO MẬT ---
def require_api_key(f):
    @wraps(f)
    def decorated(*args, **kwargs):
        # Lấy key từ Header
        key = request.headers.get('X-API-Key')
        if key != API_KEY:
            logger.warning(f"Unauthorized access from {request.remote_addr}")
            return jsonify({"error": "Unauthorized"}), 401
        return f(*args, **kwargs)
    return decorated

# --- HELPER: JSON & FILE ---
def _read_json(path, default):
    if not os.path.exists(path): return default
    try:
        with open(path, "r", encoding="utf-8") as f: return json.load(f)
    except: return default

def _atomic_write_json(path, data):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f: json.dump(data, f, ensure_ascii=False, indent=2)
    os.replace(tmp, path)

def _safe(s): return (s or "").replace("\\", "_").replace("/", "_").replace(":", "_").strip()

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
    resp.headers["Access-Control-Allow-Headers"] = "Content-Type, X-API-Key" # Cho phép gửi Key qua header
    resp.headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS"
    return resp

@app.route("/")
def home():
    return jsonify({"status": "running", "mode": "secure"})

@app.route("/browser")
def browser_page():
    if os.path.exists(os.path.join(STATIC_FOLDER, "browser.html")):
        return send_from_directory(STATIC_FOLDER, "browser.html")
    return "browser.html missing", 404

# --- API: UPLOAD (Cần Key) ---
@app.route("/upload", methods=["POST"])
@require_api_key
def upload():
    files = request.files.getlist("file")
    pid = request.form.get("projectId", "").strip()
    saved = []
    for f in files:
        if f and f.filename:
            ext = os.path.splitext(f.filename)[1].lower()
            name = f"model_{uuid.uuid4().hex}{ext}"
            f.save(os.path.join(UPLOAD_FOLDER, name))
            saved.append(name)
            if pid and ext == ".glb":
                with _lock:
                    meta = _read_json(_meta(pid), {})
                    meta["latestGlbFile"] = name
                    _atomic_write_json(_meta(pid), meta)
    
    return jsonify({"ok": True, "uploaded": saved})

@app.route("/api/projects/<pid>/models/latest-glb")
def get_latest_glb(pid):
    meta = _read_json(_meta(pid), {})
    return jsonify({"latestGlbFile": meta.get("latestGlbFile")})

# --- API: BROWSER INDEX (Cần Key) ---
@app.route("/api/projects/<pid>/browser-index", methods=["POST"])
@require_api_key
def push_index(pid):
    data = request.get_json(force=True)
    data["serverReceivedAt"] = datetime.utcnow().isoformat() + "Z"
    with _lock: _atomic_write_json(_browser(pid), data)
    logger.info(f"Index updated for {pid}")
    return jsonify({"ok": True})

@app.route("/api/projects/<pid>/browser-index/latest")
def get_index(pid):
    data = _read_json(_browser(pid), None)
    return jsonify(data) if data else ("Not found", 404)

# --- API: COMMANDS (Web -> Revit) (Cần Key) ---
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
    logger.info(f"Command pushed: {cmd['id']}")
    return jsonify({"ok": True, "id": cmd["id"]})

@app.route("/api/projects/<pid>/commands/pull")
def pull_cmds(pid):
    # Pull thường xuyên gọi, để public hoặc thêm key ở Client nếu muốn
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

# --- API: RESULTS (Revit -> Web) (Cần Key) ---
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

# --- API: SNAPSHOTS (Cần Key) ---
@app.route("/api/projects/<pid>/snapshots/upload", methods=["POST"])
@require_api_key
def up_snap(pid):
    f = request.files.get("file")
    if f:
        name = f"snap_{uuid.uuid4().hex}.png"
        f.save(os.path.join(UPLOAD_FOLDER, name))
        return jsonify({"ok": True, "url": url_for("serve_uploads", filename=name, _external=True)})
    return "No file", 400

@app.route("/uploads/<path:filename>")
def serve_uploads(filename):
    return send_from_directory(UPLOAD_FOLDER, filename)

@socketio.on("subscribe")
def on_sub(data):
    pid = (data or {}).get('projectId')
    if pid: join_room(_room(pid))

if __name__ == "__main__":
    # In ra Key để dễ debug
    print(f"--- SERVER STARTED ---")
    print(f"API KEY: {API_KEY}") 
    socketio.run(app, host="127.0.0.1", port=5000, debug=True, use_reloader=False)