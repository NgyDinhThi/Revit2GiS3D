import os
import json
import uuid
import time
import traceback
import threading
from flask import Flask, request, render_template, jsonify, send_from_directory, url_for

BASE_DIR = os.path.dirname(os.path.abspath(__file__))

UPLOAD_FOLDER = os.path.join(BASE_DIR, "uploads")
TEMPLATES_DIR = os.path.join(BASE_DIR, "templates")
STATIC_DIR = os.path.join(BASE_DIR, "static")

os.makedirs(UPLOAD_FOLDER, exist_ok=True)

app = Flask(__name__, template_folder=TEMPLATES_DIR, static_folder=STATIC_DIR, static_url_path="/static")
app.config["UPLOAD_FOLDER"] = UPLOAD_FOLDER

# =========================
# Persistent storage helpers
# =========================
_lock = threading.Lock()

def _browser_index_path(project_id: str) -> str:
    return os.path.join(UPLOAD_FOLDER, f"browser_{project_id}_latest.json")

def _commands_path(project_id: str) -> str:
    return os.path.join(UPLOAD_FOLDER, f"commands_{project_id}.json")

def _read_json(path: str, default):
    if not os.path.exists(path):
        return default
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)

def _atomic_write_json(path: str, data):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    os.replace(tmp, path)

# In-memory cache (optional)
browser_latest = {}   # projectId -> dict
command_cache = {}    # projectId -> list[dict]


# =========================
# Pages
# =========================
@app.route("/")
def home():
    return jsonify({
        "ok": True,
        "routes": {
            "upload": "/upload",
            "viewer_glb": "/viewer_glb?file=<name.glb>",
            "viewer_json": "/viewer_json?file=<name.json>",
            "browser": "/browser?projectId=P001",
            "data": "/data/<filename>",
            "api_browser_latest": "/api/projects/<projectId>/browser-index/latest",
            "api_commands_pull": "/api/projects/<projectId>/commands/pull?clientId=test"
        }
    })

@app.route("/browser")
def browser_page():
    # ưu tiên static/browser.html, nếu không có thì templates/browser.html
    static_path = os.path.join(STATIC_DIR, "browser.html")
    if os.path.exists(static_path):
        return send_from_directory(STATIC_DIR, "browser.html")
    return render_template("browser.html")

# =========================
# Viewer routes (giữ như bạn đang dùng)
# =========================
@app.route("/viewer_glb")
def viewer_glb():
    # viewer_glb.html ở templates
    return render_template("viewer_glb.html")

@app.route("/viewer_json")
def viewer_json():
    # viewer_json.html ở templates
    return render_template("viewer_json.html")

# Serve uploaded files (giữ route /data như app.py của bạn)
@app.route("/data/<path:filename>")
def serve_data(filename):
    return send_from_directory(app.config["UPLOAD_FOLDER"], filename)

# (optional) alias /uploads
@app.route("/uploads/<path:filename>")
def serve_uploads(filename):
    return send_from_directory(app.config["UPLOAD_FOLDER"], filename)


# =========================
# Upload API
# =========================
@app.route("/upload", methods=["POST"])
def upload_file():
    try:
        uploaded_filenames = []

        # 1) Nhận JSON raw
        if request.is_json:
            geojson_data = request.get_json()
            filename_json = f"model_{uuid.uuid4().hex}.json"
            output_path = os.path.join(app.config["UPLOAD_FOLDER"], filename_json)
            with open(output_path, "w", encoding="utf-8") as f:
                json.dump(geojson_data, f, ensure_ascii=False, indent=2)
            uploaded_filenames.append(filename_json)

        # 2) Nhận file multipart
        elif "file" in request.files:
            files = request.files.getlist("file")
            for file in files:
                if not file or file.filename == "":
                    continue
                ext = os.path.splitext(file.filename)[1].lower()
                if ext not in [".glb", ".json"]:
                    # vẫn cho lưu, nhưng bạn nên ưu tiên .glb/.json
                    pass
                filename = f"model_{uuid.uuid4().hex}{ext}"
                output_path = os.path.join(app.config["UPLOAD_FOLDER"], filename)
                file.save(output_path)
                uploaded_filenames.append(filename)
        else:
            return jsonify({"error": "No valid file received."}), 400

        glb_file = next((f for f in uploaded_filenames if f.lower().endswith(".glb")), None)
        json_file = next((f for f in uploaded_filenames if f.lower().endswith(".json")), None)

        viewer_url = None
        if glb_file:
            viewer_url = url_for("viewer_glb", file=glb_file, _external=True)
        elif json_file:
            viewer_url = url_for("viewer_json", file=json_file, _external=True)

        if not viewer_url:
            return jsonify({"error": "Upload thành công nhưng không có file .glb/.json để xem."}), 400

        return jsonify({
            "viewer_url": viewer_url,
            "uploaded_files": uploaded_filenames
        })

    except Exception as e:
        traceback.print_exc()
        return jsonify({"error": str(e)}), 500


# =========================
# Project Browser Index API (persist)
# =========================
@app.route("/api/projects/<project_id>/browser-index", methods=["POST"])
def push_browser_index(project_id):
    data = request.get_json(force=True)
    if not isinstance(data, dict):
        return jsonify({"error": "Invalid JSON payload"}), 400

    with _lock:
        browser_latest[project_id] = data
        _atomic_write_json(_browser_index_path(project_id), data)

    return jsonify({"ok": True})

@app.route("/api/projects/<project_id>/browser-index/latest", methods=["GET"])
def get_browser_index_latest(project_id):
    with _lock:
        if project_id in browser_latest:
            return jsonify(browser_latest[project_id])

        data = _read_json(_browser_index_path(project_id), None)
        if data is None:
            return jsonify({"error": "not found"}), 404

        browser_latest[project_id] = data
        return jsonify(data)


# =========================
# Commands API (persist)  ✅ FIX KHẢ NĂNG B
# =========================
@app.route("/api/projects/<project_id>/commands", methods=["POST"])
def push_command(project_id):
    cmd = request.get_json(force=True)
    if not isinstance(cmd, dict):
        return jsonify({"error": "Invalid JSON payload"}), 400

    cmd["id"] = str(uuid.uuid4())
    cmd["ts"] = time.time()

    with _lock:
        cmds = command_cache.get(project_id)
        if cmds is None:
            cmds = _read_json(_commands_path(project_id), [])
        cmds.append(cmd)
        command_cache[project_id] = cmds
        _atomic_write_json(_commands_path(project_id), cmds)

    return jsonify({"ok": True, "id": cmd["id"]})

@app.route("/api/projects/<project_id>/commands/pull", methods=["GET"])
def pull_commands(project_id):
    # MVP: trả tất cả command chưa ack (ở đây là toàn bộ list)
    with _lock:
        cmds = command_cache.get(project_id)
        if cmds is None:
            cmds = _read_json(_commands_path(project_id), [])
            command_cache[project_id] = cmds

        return jsonify({"commands": cmds})

@app.route("/api/projects/<project_id>/commands/ack", methods=["POST"])
def ack_commands(project_id):
    body = request.get_json(force=True)
    if not isinstance(body, dict):
        return jsonify({"error": "Invalid JSON payload"}), 400

    ack_ids = set(body.get("ids", []))

    with _lock:
        cmds = command_cache.get(project_id)
        if cmds is None:
            cmds = _read_json(_commands_path(project_id), [])

        if ack_ids:
            cmds = [c for c in cmds if c.get("id") not in ack_ids]

        command_cache[project_id] = cmds
        _atomic_write_json(_commands_path(project_id), cmds)

    return jsonify({"ok": True})


# =========================
# Run (tắt reloader để không reset)
# =========================
if __name__ == "__main__":
    app.run(host="127.0.0.1", port=5000, debug=False, use_reloader=False)
