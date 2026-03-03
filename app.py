import os
import uuid
import logging
import threading 
import io
from datetime import datetime
from functools import wraps
from flask import Flask, request, jsonify, send_from_directory, send_file
from flask_socketio import SocketIO, join_room, emit
from dotenv import load_dotenv

# Load biến môi trường
load_dotenv()

# --- CẤU HÌNH ---
logging.basicConfig(level=logging.INFO, format='%(asctime)s %(levelname)s: %(message)s')
logger = logging.getLogger(__name__)

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
STATIC_FOLDER = os.path.join(BASE_DIR, "static")
TEMPLATES_FOLDER = os.path.join(BASE_DIR, "templates")

# Không tạo thư mục uploads nữa
for folder in [STATIC_FOLDER, TEMPLATES_FOLDER]:
    os.makedirs(folder, exist_ok=True)

app = Flask(__name__, static_folder=STATIC_FOLDER, static_url_path="/static", template_folder=TEMPLATES_FOLDER)
app.config['MAX_CONTENT_LENGTH'] = 1000 * 1024 * 1024  # 1GB Limit

class MutePullLogsFilter(logging.Filter):
    def filter(self, record):
        return 'commands/pull' not in record.getMessage()

logging.getLogger('werkzeug').addFilter(MutePullLogsFilter())

API_KEY = os.getenv("API_KEY", "CHANGE-ME-IN-PRODUCTION")
socketio = SocketIO(app, cors_allowed_origins='*')
_lock = threading.Lock()

# =========================================================
# 🚀 HỆ THỐNG LƯU TRỮ TRÊN RAM (IN-MEMORY DATABASE)
# Không ghi bất cứ thứ gì ra ổ cứng!
# =========================================================
IN_MEMORY_DB = {}

def get_proj(pid):
    if pid not in IN_MEMORY_DB:
        IN_MEMORY_DB[pid] = {
            "browser": None,
            "commands": [],
            "glb_data": None,
            "images": {}
        }
    return IN_MEMORY_DB[pid]
# =========================================================

def require_api_key(f):
    @wraps(f)
    def decorated(*args, **kwargs):
        key = request.headers.get('X-API-Key')
        if key != API_KEY:
            logger.warning(f"Unauthorized access from {request.remote_addr}")
            return jsonify({"error": "Unauthorized"}), 401
        return f(*args, **kwargs)
    return decorated

def _room(pid): return f"proj:{pid}"

# --- ROUTES ---
@app.after_request
def add_cors(resp):
    resp.headers["Access-Control-Allow-Origin"] = "*"
    resp.headers["Access-Control-Allow-Headers"] = "Content-Type, X-API-Key"
    resp.headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS"
    return resp

@app.route("/")
def home():
    return jsonify({"status": "running", "mode": "RAM-ONLY", "api_key_hint": "Check C# settings"})

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

# --- UPLOAD API (LƯU VÀO RAM) ---
@app.route("/upload", methods=["POST"])
@require_api_key
def upload():
    files = request.files.getlist("file")
    project_id = request.form.get("projectId", "").strip()
    
    if not project_id:
        return jsonify({"error": "Missing Project ID"}), 400

    for f in files:
        if f and f.filename.endswith('.glb'):
            with _lock:
                proj = get_proj(project_id)
                # Đọc dữ liệu file trực tiếp vào thanh RAM của máy chủ
                proj["glb_data"] = f.read() 
            logger.info(f"Đã cập nhật Mô hình 3D vào RAM cho dự án: {project_id}")

    return jsonify({"ok": True})

# --- TRẢ GLB TỪ RAM CHO WEB ---
@app.route("/api/projects/<pid>/models/model.glb")
def download_glb(pid):
    proj = get_proj(pid)
    glb_data = proj.get("glb_data")
    if not glb_data:
        return "No model data", 404
    
    # Biến byte trong RAM thành 1 file ảo gửi trả cho Web Viewer
    return send_file(io.BytesIO(glb_data), mimetype="model/gltf-binary", download_name="model.glb", as_attachment=False)

@app.route("/api/projects/<pid>/models/latest-glb")
def get_latest_glb(pid):
    proj = get_proj(pid)
    if not proj.get("glb_data"):
        return jsonify({"error": "No model found. Please 'Publish' from Revit."}), 404
    
    return jsonify({
        # Truyền đường dẫn có đuôi .glb ảo
        "latestGlbFile": f"/api/projects/{pid}/models/model.glb"
    })

# --- BROWSER INDEX (LƯU VÀO RAM) ---
@app.route("/api/projects/<pid>/browser-index", methods=["POST"])
@require_api_key
def push_index(pid):
    data = request.get_json(force=True)
    data.setdefault("projectId", pid)
    with _lock: 
        get_proj(pid)["browser"] = data
    return jsonify({"ok": True})

@app.route("/api/projects/<pid>/browser-index/latest")
def get_index(pid):
    data = get_proj(pid).get("browser")
    return jsonify(data) if data else ("Not found", 404)

# --- COMMANDS (LƯU VÀO RAM) ---
@app.route("/api/projects/<pid>/commands", methods=["POST"])
@require_api_key
def push_cmd(pid):
    cmd = request.get_json(force=True)
    cmd["id"] = str(uuid.uuid4())
    with _lock:
        get_proj(pid)["commands"].append(cmd)
    socketio.emit("command", cmd, room=_room(pid))
    return jsonify({"ok": True, "id": cmd["id"]})

@app.route("/api/projects/<pid>/commands/pull")
def pull_cmds(pid):
    return jsonify({"commands": get_proj(pid)["commands"]})

@app.route("/api/projects/<pid>/commands/ack", methods=["POST"])
@require_api_key
def ack_cmds(pid):
    # [ĐÃ SỬA]: XÓA BỎ CƠ CHẾ HỦY LỆNH ĐỂ LƯU NHẬT KÝ
    # Các lệnh (commands) vẫn được giữ lại trong RAM vĩnh viễn 
    # để đề phòng Revit bị tắt không lưu thì vẫn có thể tải lại
    return jsonify({"ok": True})

# --- RESULTS ---
@app.route("/api/projects/<pid>/command-results", methods=["POST"])
@require_api_key
def post_res(pid):
    data = request.get_json(force=True)
    
    if data and data.get("id"):
        # NÂNG CẤP TỐC ĐỘ: Tách cục dữ liệu ảnh khổng lồ ra khỏi WebSocket
        if "imageUrl" in data and data["imageUrl"].startswith("data:image"):
            import base64
            cmd_id = data["id"]
            
            # Cắt lấy phần dữ liệu thực (bỏ chữ data:image/png;base64, đi)
            header, encoded = data["imageUrl"].split(",", 1)
            mime_type = header.split(";")[0].split(":")[1]
            
            # Giải mã ngược về dữ liệu ảnh gốc (Raw Bytes) và cất vào RAM
            raw_bytes = base64.b64decode(encoded)
            get_proj(pid).setdefault("images", {})[cmd_id] = {
                "mime": mime_type,
                "bytes": raw_bytes
            }
            
            # Thay thế cục Base64 nặng nề bằng 1 đường link siêu nhẹ để đẩy qua Socket
            data["imageUrl"] = f"/api/projects/{pid}/images/{cmd_id}"

        # Lúc này data cực kỳ nhẹ, Socket bắn qua Web trong chớp mắt
        socketio.emit("command_result", data, room=_room(pid))
    return jsonify({"ok": True})

@app.route("/api/projects/<pid>/images/<cmd_id>")
def get_image_from_ram(pid, cmd_id):
    proj = get_proj(pid)
    img_obj = proj.get("images", {}).get(cmd_id)
    if not img_obj:
        return "Not found", 404
    # Bơm thẳng dữ liệu byte từ RAM ra màn hình trình duyệt
    return send_file(io.BytesIO(img_obj["bytes"]), mimetype=img_obj["mime"])

@socketio.on("subscribe")
def on_sub(data):
    pid = (data or {}).get('projectId')
    if pid: join_room(_room(pid))

if __name__ == "__main__":
    print(f"--- SERVER STARTED on 5000 (RAM-ONLY MODE) ---")
    print(f"API KEY: {API_KEY}") 
    socketio.run(app, host="0.0.0.0", port=5000, debug=True, use_reloader=False)