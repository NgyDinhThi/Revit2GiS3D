import os
import uuid
import logging
import threading 
import io
import json
import urllib.parse
from datetime import datetime
from functools import wraps
from flask import Flask, request, jsonify, send_from_directory, send_file
from flask_socketio import SocketIO, join_room, emit
from dotenv import load_dotenv

load_dotenv()
logging.basicConfig(level=logging.INFO, format='%(asctime)s %(levelname)s: %(message)s')
logger = logging.getLogger(__name__)

# --- CẤU HÌNH THƯ MỤC ---
BASE_DIR = os.path.dirname(os.path.abspath(__file__))
STATIC_FOLDER = os.path.join(BASE_DIR, "static")
TEMPLATES_FOLDER = os.path.join(BASE_DIR, "templates")
UPLOADS_FOLDER = os.path.join(BASE_DIR, "uploads") 
DB_FILE = os.path.join(UPLOADS_FOLDER, "database_v2.json") # File lưu trữ bền vững

for folder in [STATIC_FOLDER, TEMPLATES_FOLDER, UPLOADS_FOLDER]:
    os.makedirs(folder, exist_ok=True)

app = Flask(__name__, static_folder=STATIC_FOLDER, static_url_path="/static", template_folder=TEMPLATES_FOLDER)
app.config['MAX_CONTENT_LENGTH'] = 2000 * 1024 * 1024  

# Loại bỏ log spam từ polling của Revit
logging.getLogger('werkzeug').addFilter(lambda r: 'commands/pull' not in r.getMessage())

API_KEY = os.getenv("API_KEY", "CHANGE-ME-IN-PRODUCTION")
socketio = SocketIO(app, cors_allowed_origins='*')
_lock = threading.Lock()

# --- QUẢN LÝ DỮ LIỆU ---
IN_MEMORY_DB = {}

def load_db():
    global IN_MEMORY_DB
    IN_MEMORY_DB = {}
    
    # Bước 1: Migrate từ file cũ (nếu có)
    if os.path.exists(DB_FILE):
        try:
            with open(DB_FILE, "r", encoding="utf-8") as f:
                IN_MEMORY_DB = json.load(f)
            logger.info("Đã tải dữ liệu từ database_v2.json để migrate")
            # Đổi tên file cũ để không đọc lại lần sau
            os.rename(DB_FILE, DB_FILE + ".bak")
            # Lưu ngay ra các file dự án
            save_db()
        except Exception as e:
            logger.error(f"Lỗi migrate DB cũ: {e}")
            return
            
    # Bước 2: Đọc từ các tệp dự án riêng lẻ
    for pid in os.listdir(UPLOADS_FOLDER):
        pid_folder = os.path.join(UPLOADS_FOLDER, pid)
        if os.path.isdir(pid_folder):
            db_path = os.path.join(pid_folder, "database.json")
            if os.path.exists(db_path):
                try:
                    with open(db_path, "r", encoding="utf-8") as f:
                        IN_MEMORY_DB[pid] = json.load(f)
                except Exception as e:
                    logger.error(f"Lỗi tải DB cho dự án {pid}: {e}")

def save_db():
    try:
        with _lock:
            for pid, data in IN_MEMORY_DB.items():
                pid_folder = os.path.join(UPLOADS_FOLDER, pid)
                os.makedirs(pid_folder, exist_ok=True)
                
                db_path = os.path.join(pid_folder, "database.json")
                temp_file = db_path + ".tmp"
                with open(temp_file, "w", encoding="utf-8") as f:
                    json.dump(data, f, ensure_ascii=False, indent=4)
                os.replace(temp_file, db_path)
    except Exception as e:
        logger.error(f"Lỗi lưu DB: {e}")

def get_proj(pid):
    if pid not in IN_MEMORY_DB:
        IN_MEMORY_DB[pid] = { 
            "browser": None, 
            "commands": [], 
            "final_state": {}, # Lưu giá trị tham số mới nhất của từng Element
            "rvt_name": "", 
            "images": {}, 
            "history": [] 
        }
    return IN_MEMORY_DB[pid]

def log_history(pid, user, action, details):
    proj = get_proj(pid)
    if "history" not in proj:
        proj["history"] = []
    time_str = datetime.now().strftime("%d/%m/%Y %H:%M:%S")
    proj["history"].insert(0, {"time": time_str, "user": user, "action": action, "details": details})
    save_db()

# --- MIDDLEWARE ---
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

# --- ROUTES GIAO DIỆN ---
@app.route("/browser")
def browser_page(): return send_from_directory(STATIC_FOLDER, "browser.html")

@app.route("/viewer_glb")
def viewer_glb(): return send_from_directory(TEMPLATES_FOLDER, "viewer_glb.html")

@app.route("/history")
def history_page(): return send_from_directory(STATIC_FOLDER, "historyWindow.html")

# --- API ĐỒNG BỘ FILE & MÔ HÌNH ---
@app.route("/upload", methods=["POST"])
@require_api_key
def upload():
    files = request.files.getlist("file")
    project_id = request.form.get("projectId", "").strip()
    proj_dir = os.path.join(UPLOADS_FOLDER, project_id)
    os.makedirs(proj_dir, exist_ok=True)

    has_rvt = False # 1. Khởi tạo cờ kiểm tra xem lần này có file Revit không

    for f in files:
        if f:
            if f.filename.endswith('.glb'):
                f.save(os.path.join(proj_dir, "model.glb"))
            elif f.filename.endswith('.rvt'):
                f.save(os.path.join(proj_dir, f.filename))
                with _lock: 
                    get_proj(project_id)["rvt_name"] = f.filename
                has_rvt = True # 2. Bật cờ nếu phát hiện có đính kèm file
    if not has_rvt:
        with _lock:
            get_proj(project_id)["rvt_name"] = "" # Xóa tên file cũ khỏi Database
            
        # (Tùy chọn) Xóa luôn file .rvt cũ trong ổ cứng server cho nhẹ máy
        for old_file in os.listdir(proj_dir):
            if old_file.endswith('.rvt'):
                try:
                    os.remove(os.path.join(proj_dir, old_file))
                except Exception as e:
                    logger.error(f"Không thể xóa file Revit cũ: {e}")

    save_db()
    return jsonify({"ok": True})

@app.route("/api/projects/<pid>/models/latest-glb")
def get_latest_glb(pid):
    if os.path.exists(os.path.join(UPLOADS_FOLDER, pid, "model.glb")):
        return jsonify({"latestGlbFile": f"/api/projects/{pid}/models/model.glb"})
    return "No model found.", 404

@app.route("/api/projects/<pid>/models/model.glb")
def download_glb(pid):
    path = os.path.join(UPLOADS_FOLDER, pid, "model.glb")
    return send_file(path) if os.path.exists(path) else ("Not found", 404)

@app.route("/api/projects/<pid>/browser-index", methods=["POST"])
@require_api_key
def push_index(pid):
    data = request.get_json(force=True)
    with _lock: 
        proj = get_proj(pid)
        proj["browser"] = data
        proj["final_state"] = {}       
    user = urllib.parse.unquote(request.headers.get("X-User-Name", "Ẩn danh"))
    log_history(pid, user, "PUBLISH", "Đã đồng bộ dữ liệu Revit mới nhất lên Web.")
    return jsonify({"ok": True})

@app.route("/api/projects/<pid>/browser-index/latest")
def get_index(pid): 
    proj = get_proj(pid)
    browser_data = proj.get("browser")  
    if not browser_data:
        return ("Not found", 404)
        
    final_state = proj.get("final_state", {})    
    if final_state:
        # Cập nhật tên hiển thị trong Tree Nodes
        if "nodes" in browser_data:
            for node in browser_data["nodes"]:
                uid = node.get("revit", {}).get("uniqueId")       
                if uid and uid in final_state:
                    params = final_state[uid]
                    new_name = params.get("View Name") or params.get("Sheet Name") or params.get("Name")
                    if new_name:
                        node["title"] = new_name  # Ghi đè tên hiển thị
                        
        # Cập nhật giá trị hiển thị trong bảng Properties
        if "elements" in browser_data:
            for uid, updated_params in final_state.items():
                if uid in browser_data["elements"]:
                    props = browser_data["elements"][uid].get("properties", {})
                    for p_name, p_val in updated_params.items():
                        updated = False
                        # Tìm và ghi đè giá trị nếu tham số nằm trong một nhóm
                        for group_name, group_props in props.items():
                            if isinstance(group_props, dict):
                                if p_name in group_props:
                                    group_props[p_name] = p_val
                                    updated = True
                                    break
                        # Nếu ko tìm thấy trong nhóm nào, thêm vào mặc định
                        if not updated:
                            if all(not isinstance(v, dict) for v in props.values()):
                                props[p_name] = p_val
                            else:
                                if "Identity Data" not in props:
                                    props["Identity Data"] = {}
                                props["Identity Data"][p_name] = p_val
                                
    return jsonify(browser_data)

# --- QUẢN LÝ LỆNH & TRẠNG THÁI (CORE LOGIC) ---
@app.route("/api/projects/<pid>/commands", methods=["POST"])
@require_api_key
def push_cmd(pid):
    cmd = request.get_json(force=True)
    cmd["id"] = str(uuid.uuid4())
    proj = get_proj(pid)
    
    with _lock:
        proj["commands"].append(cmd)
        # Cập nhật trạng thái cuối cùng để Revit có thể "hồi phục" dữ liệu
        if cmd.get("action") == "update_parameter":
            tid = cmd.get("targetUniqueId")
            params = cmd.get("parameters", {})
            if tid:
                proj["final_state"].setdefault(tid, {}).update(params)
    
    socketio.emit("command", cmd, room=_room(pid))
    details = f"Lệnh: {cmd.get('action')}"
    if cmd.get("action") == "update_parameter":
        details = f"Sửa tham số: {cmd.get('parameters')}"
    
    log_history(pid, cmd.get("user", "Web User"), "UPDATE", details)
    return jsonify({"ok": True, "id": cmd["id"]})

@app.route("/api/projects/<pid>/commands/pull")
def pull_cmds(pid): 
    return jsonify({"commands": get_proj(pid)["commands"]})

@app.route("/api/projects/<pid>/commands/sync-state")
def sync_state(pid):
    """API để Revit tải lại toàn bộ các thay đổi từ Web khi mở file"""
    return jsonify({"final_state": get_proj(pid).get("final_state", {})})

@app.route("/api/projects/<pid>/commands/ack", methods=["POST"])
@require_api_key
def ack_cmds(pid): 
    data = request.get_json(force=True)
    ack_ids = data.get("ids", [])
    if ack_ids:
        with _lock:
            proj = get_proj(pid)
            proj["commands"] = [c for c in proj["commands"] if c.get("id") not in ack_ids]
    save_db()
    return jsonify({"ok": True})

@app.route("/api/projects/<pid>/command-results", methods=["POST"])
@require_api_key
def post_res(pid):
    data = request.get_json(force=True)
    if data and data.get("id"):
        # Xử lý ảnh render JPEG gửi về dưới dạng Base64
        if "imageUrl" in data and data["imageUrl"].startswith("data:image"):
            import base64
            cmd_id = data["id"]
            header, encoded = data["imageUrl"].split(",", 1)
            raw_bytes = base64.b64decode(encoded)
            
            # Trích xuất mime type từ header
            mime = "image/jpeg"
            if "image/" in header:
                mime = header.split(";")[0].split(":")[1]
            
            # Map MIME type sang phần mở rộng file
            ext = "jpg"
            if "png" in mime:
                ext = "png"
            elif "gif" in mime:
                ext = "gif"
                
            # Lưu file ảnh vào thư mục uploads/<pid>/images/
            img_dir = os.path.join(UPLOADS_FOLDER, pid, "images")
            os.makedirs(img_dir, exist_ok=True)
            filename = f"{cmd_id}.{ext}"
            img_path = os.path.join(img_dir, filename)
            with open(img_path, "wb") as img_file:
                img_file.write(raw_bytes)
                
            # Lưu metadata vào database
            with _lock:
                get_proj(pid)["images"][cmd_id] = { "mime": mime, "filename": filename }
            save_db()
            
            data["imageUrl"] = f"/api/projects/{pid}/images/{cmd_id}"
        socketio.emit("command_result", data, room=_room(pid))
    return jsonify({"ok": True})

@app.route("/api/projects/<pid>/images/<cmd_id>")
def get_image(pid, cmd_id):
    img = get_proj(pid).get("images", {}).get(cmd_id)
    if not img:
        return "404", 404
    if "filename" in img:
        path = os.path.join(UPLOADS_FOLDER, pid, "images", img["filename"])
        if os.path.exists(path):
            return send_file(path, mimetype=img["mime"])
    elif "bytes" in img:
        return send_file(io.BytesIO(img["bytes"]), mimetype=img["mime"])
    return "404", 404
@app.route("/api/projects/<pid>/history")
def get_history(pid):
    """API trả về danh sách lịch sử để hiển thị lên bảng nhật ký"""
    proj = get_proj(pid)
    return jsonify(proj.get("history", []))
@app.route("/api/projects/<pid>/models/rvt")
def download_rvt(pid):
    """API để tải file gốc Revit (.rvt) về máy tính"""
    proj = get_proj(pid)
    rvt_filename = proj.get("rvt_name")
    
    # Kiểm tra xem dự án này đã từng đính kèm file Revit chưa
    if not rvt_filename:
        return "Dự án này chưa được đính kèm file Revit.", 404
        
    path = os.path.join(UPLOADS_FOLDER, pid, rvt_filename)
    
    # Kiểm tra xem file có thực sự tồn tại trong thư mục uploads không
    if os.path.exists(path):
        # as_attachment=True giúp trình duyệt tự động tải file xuống (Download)
        return send_file(path, as_attachment=True)
    else:
        return "File Revit không tồn tại trên Server.", 404
    
# --- SOCKETIO ---
@socketio.on("subscribe")
def on_sub(data):
    if data and data.get('projectId'): join_room(_room(data['projectId']))

if __name__ == "__main__":
    load_db() # Tải dữ liệu cũ khi khởi động
    socketio.run(app, host="0.0.0.0", port=5000, debug=True, use_reloader=False)