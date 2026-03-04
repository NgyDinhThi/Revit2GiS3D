import os
import uuid
import logging
import threading 
import io
import urllib.parse  # <--- [MỚI] THÊM THƯ VIỆN DỊCH NGƯỢC URL
from datetime import datetime
from functools import wraps
from flask import Flask, request, jsonify, send_from_directory, send_file
from flask_socketio import SocketIO, join_room, emit
from dotenv import load_dotenv

load_dotenv()
logging.basicConfig(level=logging.INFO, format='%(asctime)s %(levelname)s: %(message)s')
logger = logging.getLogger(__name__)

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
STATIC_FOLDER = os.path.join(BASE_DIR, "static")
TEMPLATES_FOLDER = os.path.join(BASE_DIR, "templates")

for folder in [STATIC_FOLDER, TEMPLATES_FOLDER]:
    os.makedirs(folder, exist_ok=True)

app = Flask(__name__, static_folder=STATIC_FOLDER, static_url_path="/static", template_folder=TEMPLATES_FOLDER)
app.config['MAX_CONTENT_LENGTH'] = 1000 * 1024 * 1024  
logging.getLogger('werkzeug').addFilter(lambda r: 'commands/pull' not in r.getMessage())

API_KEY = os.getenv("API_KEY", "CHANGE-ME-IN-PRODUCTION")
socketio = SocketIO(app, cors_allowed_origins='*')
_lock = threading.Lock()

IN_MEMORY_DB = {}

def get_proj(pid):
    if pid not in IN_MEMORY_DB:
        IN_MEMORY_DB[pid] = { "browser": None, "commands": [], "glb_data": None, "images": {}, "history": [] }
    return IN_MEMORY_DB[pid]

# Hàm Ghi sổ Nam Tào
def log_history(pid, user, action, details):
    proj = get_proj(pid)
    time_str = datetime.now().strftime("%d/%m/%Y %H:%M:%S")
    proj["history"].insert(0, {"time": time_str, "user": user, "action": action, "details": details})

def require_api_key(f):
    @wraps(f)
    def decorated(*args, **kwargs):
        if request.headers.get('X-API-Key') != API_KEY: return jsonify({"error": "Unauthorized"}), 401
        return f(*args, **kwargs)
    return decorated

def _room(pid): return f"proj:{pid}"

@app.after_request
def add_cors(resp):
    resp.headers["Access-Control-Allow-Origin"] = "*"
    resp.headers["Access-Control-Allow-Headers"] = "Content-Type, X-API-Key, X-User-Name"
    resp.headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS"
    return resp

@app.route("/browser")
def browser_page(): return send_from_directory(STATIC_FOLDER, "browser.html")

@app.route("/viewer_glb")
def viewer_glb(): return send_from_directory(STATIC_FOLDER, "viewer_glb.html")

@app.route("/upload", methods=["POST"])
@require_api_key
def upload():
    files = request.files.getlist("file")
    project_id = request.form.get("projectId", "").strip()
    for f in files:
        if f and f.filename.endswith('.glb'):
            with _lock: get_proj(project_id)["glb_data"] = f.read() 
    return jsonify({"ok": True})

@app.route("/api/projects/<pid>/models/latest-glb")
def get_latest_glb(pid): return jsonify({"latestGlbFile": f"/api/projects/{pid}/models/model.glb"}) if get_proj(pid).get("glb_data") else ("No model found.", 404)

@app.route("/api/projects/<pid>/models/model.glb")
def download_glb(pid):
    data = get_proj(pid).get("glb_data")
    return send_file(io.BytesIO(data), mimetype="model/gltf-binary", download_name="model.glb", as_attachment=False) if data else ("Not found", 404)

@app.route("/api/projects/<pid>/browser-index", methods=["POST"])
@require_api_key
def push_index(pid):
    data = request.get_json(force=True)
    with _lock: get_proj(pid)["browser"] = data
    
    # --- [ĐÃ SỬA] DỊCH NGƯỢC URL VỀ LẠI TIẾNG VIỆT CHUẨN ---
    raw_user = request.headers.get("X-User-Name", "Ẩn danh")
    user = urllib.parse.unquote(raw_user)
    
    log_history(pid, user, "PUBLISH", "Đã đồng bộ dữ liệu Revit mới nhất lên Web.")
    return jsonify({"ok": True})

@app.route("/api/projects/<pid>/browser-index/latest")
def get_index(pid): return jsonify(get_proj(pid).get("browser")) or ("Not found", 404)

@app.route("/api/projects/<pid>/commands", methods=["POST"])
@require_api_key
def push_cmd(pid):
    cmd = request.get_json(force=True)
    cmd["id"] = str(uuid.uuid4())
    with _lock: get_proj(pid)["commands"].append(cmd)
    socketio.emit("command", cmd, room=_room(pid))
    
    user = cmd.get("user", "Khách trên Web")
    action = cmd.get("action", "")
    details = f"Đã ra lệnh: {action}"
    if action == "update_parameter":
        params = cmd.get("parameters", {})
        details = f"Đã sửa tham số: {', '.join([f'{k}={v}' for k,v in params.items()])}"
    
    log_history(pid, user, "UPDATE", details)
    return jsonify({"ok": True, "id": cmd["id"]})

@app.route("/api/projects/<pid>/commands/pull")
def pull_cmds(pid): return jsonify({"commands": get_proj(pid)["commands"]})

@app.route("/api/projects/<pid>/commands/ack", methods=["POST"])
@require_api_key
def ack_cmds(pid): 
    return jsonify({"ok": True})

@app.route("/api/projects/<pid>/history")
def get_history(pid):
    return jsonify(get_proj(pid)["history"])

@app.route("/api/projects/<pid>/command-results", methods=["POST"])
@require_api_key
def post_res(pid):
    data = request.get_json(force=True)
    if data and data.get("id"):
        if "imageUrl" in data and data["imageUrl"].startswith("data:image"):
            import base64
            cmd_id = data["id"]
            header, encoded = data["imageUrl"].split(",", 1)
            raw_bytes = base64.b64decode(encoded)
            get_proj(pid).setdefault("images", {})[cmd_id] = { "mime": header.split(";")[0].split(":")[1], "bytes": raw_bytes }
            data["imageUrl"] = f"/api/projects/{pid}/images/{cmd_id}"
        socketio.emit("command_result", data, room=_room(pid))
    return jsonify({"ok": True})

@app.route("/api/projects/<pid>/images/<cmd_id>")
def get_image_from_ram(pid, cmd_id):
    img_obj = get_proj(pid).get("images", {}).get(cmd_id)
    return send_file(io.BytesIO(img_obj["bytes"]), mimetype=img_obj["mime"]) if img_obj else ("Not found", 404)

@app.route("/history")
def history_page():
    return send_from_directory(STATIC_FOLDER, "historyWindow.html")

@socketio.on("subscribe")
def on_sub(data):
    if data and data.get('projectId'): join_room(_room(data['projectId']))

if __name__ == "__main__":
    print(f"--- SERVER STARTED on 5000 (RAM-ONLY MODE) ---")
    socketio.run(app, host="0.0.0.0", port=5000, debug=True, use_reloader=False)