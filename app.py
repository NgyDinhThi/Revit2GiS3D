import os
import json
import uuid
import time
import threading
from datetime import datetime
from flask import Flask, request, jsonify, send_from_directory, url_for

BASE_DIR = os.path.dirname(os.path.abspath(__file__))

UPLOAD_FOLDER = os.path.join(BASE_DIR, "uploads")
STATIC_FOLDER = os.path.join(BASE_DIR, "static")
TEMPLATES_FOLDER = os.path.join(BASE_DIR, "templates")

os.makedirs(UPLOAD_FOLDER, exist_ok=True)
os.makedirs(STATIC_FOLDER, exist_ok=True)
os.makedirs(TEMPLATES_FOLDER, exist_ok=True)

app = Flask(
    __name__,
    static_folder=STATIC_FOLDER,
    static_url_path="/static",
    template_folder=TEMPLATES_FOLDER
)

_lock = threading.Lock()

# =========================
# Small JSON persistence helpers
# =========================
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

def _safe(s: str) -> str:
    return (s or "").replace("\\", "_").replace("/", "_").replace(":", "_").strip()

def _browser_index_path(project_id: str) -> str:
    return os.path.join(UPLOAD_FOLDER, f"browser_{_safe(project_id)}_latest.json")

def _commands_path(project_id: str) -> str:
    return os.path.join(UPLOAD_FOLDER, f"commands_{_safe(project_id)}.json")

def _cmd_result_path(project_id: str, cmd_id: str) -> str:
    return os.path.join(UPLOAD_FOLDER, f"cmdres_{_safe(project_id)}_{_safe(cmd_id)}.json")

def _project_meta_path(project_id: str) -> str:
    return os.path.join(UPLOAD_FOLDER, f"project_{_safe(project_id)}_meta.json")


# =========================
# In-memory caches (optional)
# =========================
browser_latest = {}   # projectId -> dict
command_cache = {}    # projectId -> list[dict]
# cmd results not cached; file is enough


# =========================
# CORS (handy)
# =========================
@app.after_request
def add_cors_headers(resp):
    resp.headers["Access-Control-Allow-Origin"] = "*"
    resp.headers["Access-Control-Allow-Headers"] = "Content-Type"
    resp.headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS"
    return resp


# =========================
# Pages
# =========================
@app.route("/")
def home():
    return jsonify({
        "ok": True,
        "endpoints": {
            "browser": "/browser?projectId=P001",
            "upload_model": "/upload",
            "upload_snapshot": "/api/projects/<projectId>/snapshots/upload",
            "browser_index_push": "/api/projects/<projectId>/browser-index",
            "browser_index_latest": "/api/projects/<projectId>/browser-index/latest",
            "commands_push": "/api/projects/<projectId>/commands",
            "commands_pull": "/api/projects/<projectId>/commands/pull?clientId=test",
            "commands_ack": "/api/projects/<projectId>/commands/ack",
            "cmd_result_post": "/api/projects/<projectId>/command-results",
            "cmd_result_get": "/api/projects/<projectId>/command-results/<cmdId>",
            "viewer_glb": "/viewer_glb?file=<name.glb>",
            "viewer_json": "/viewer_json?file=<name.json>",
            "uploads": "/uploads/<filename>"
        }
    })

@app.route("/browser")
def browser_page():
    # Prefer static/browser.html (you have it)
    path = os.path.join(STATIC_FOLDER, "browser.html")
    if os.path.exists(path):
        return send_from_directory(STATIC_FOLDER, "browser.html")
    return jsonify({"error": "browser.html not found in static/"}), 404

@app.route("/viewer_glb")
def viewer_glb():
    # Prefer static/viewer_glb.html if exists, else templates
    path = os.path.join(STATIC_FOLDER, "viewer_glb.html")
    if os.path.exists(path):
        return send_from_directory(STATIC_FOLDER, "viewer_glb.html")
    path2 = os.path.join(TEMPLATES_FOLDER, "viewer_glb.html")
    if os.path.exists(path2):
        return send_from_directory(TEMPLATES_FOLDER, "viewer_glb.html")
    return jsonify({"error": "viewer_glb.html not found"}), 404

@app.route("/viewer_json")
def viewer_json():
    path = os.path.join(STATIC_FOLDER, "viewer_json.html")
    if os.path.exists(path):
        return send_from_directory(STATIC_FOLDER, "viewer_json.html")
    path2 = os.path.join(TEMPLATES_FOLDER, "viewer_json.html")
    if os.path.exists(path2):
        return send_from_directory(TEMPLATES_FOLDER, "viewer_json.html")
    return jsonify({"error": "viewer_json.html not found"}), 404


# =========================
# Serve uploaded files
# =========================
@app.route("/uploads/<path:filename>")
def serve_uploads(filename):
    return send_from_directory(UPLOAD_FOLDER, filename)

# alias (if you used /data before)
@app.route("/data/<path:filename>")
def serve_data(filename):
    return send_from_directory(UPLOAD_FOLDER, filename)


# =========================
# Upload model files (GLB/JSON)
# Optional: can include projectId to remember "latest glb"
# =========================
@app.route("/upload", methods=["POST"])
def upload():
    files = request.files.getlist("file")
    if not files:
        return jsonify({"error": "No file field named 'file' in multipart form-data."}), 400

    project_id = request.form.get("projectId", "").strip()
    saved = []

    for f in files:
        if f is None or not f.filename:
            continue

        ext = os.path.splitext(f.filename)[1].lower()
        if ext not in [".glb", ".json"]:
            # still save, but viewer only supports glb/json
            pass

        name = f"model_{uuid.uuid4().hex}{ext if ext else ''}"
        save_path = os.path.join(UPLOAD_FOLDER, name)
        f.save(save_path)
        saved.append(name)

        # remember latest glb if projectId provided
        if project_id and ext == ".glb":
            with _lock:
                meta = _read_json(_project_meta_path(project_id), {})
                meta["latestGlbFile"] = name
                meta["updatedAt"] = datetime.utcnow().isoformat() + "Z"
                _atomic_write_json(_project_meta_path(project_id), meta)

    if not saved:
        return jsonify({"error": "No valid files received."}), 400

    glb_file = next((x for x in saved if x.lower().endswith(".glb")), None)
    json_file = next((x for x in saved if x.lower().endswith(".json")), None)

    viewer_url = None
    if glb_file:
        viewer_url = url_for("viewer_glb", file=glb_file, _external=True)
    elif json_file:
        viewer_url = url_for("viewer_json", file=json_file, _external=True)

    return jsonify({"ok": True, "viewer_url": viewer_url, "uploaded": saved})


@app.route("/api/projects/<project_id>/models/latest-glb", methods=["GET"])
def get_latest_glb(project_id):
    with _lock:
        meta = _read_json(_project_meta_path(project_id), {})
    glb = meta.get("latestGlbFile")
    if not glb:
        return jsonify({"error": "no latest glb for this project"}), 404
    return jsonify({"latestGlbFile": glb, "url": url_for("serve_uploads", filename=glb, _external=True)})


# =========================
# Browser Index API (persist)
# =========================
@app.route("/api/projects/<project_id>/browser-index", methods=["POST"])
def push_browser_index(project_id):
    data = request.get_json(force=True)
    if not isinstance(data, dict):
        return jsonify({"error": "Invalid JSON payload."}), 400

    data.setdefault("projectId", project_id)
    data.setdefault("serverReceivedAt", datetime.utcnow().isoformat() + "Z")

    with _lock:
        browser_latest[project_id] = data
        _atomic_write_json(_browser_index_path(project_id), data)

    nodes_count = len(data.get("nodes", [])) if isinstance(data.get("nodes"), list) else 0
    return jsonify({"ok": True, "nodes": nodes_count})

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
# Commands API (persist) - Web -> Revit
# =========================
@app.route("/api/projects/<project_id>/commands", methods=["POST"])
def push_command(project_id):
    cmd = request.get_json(force=True)
    if not isinstance(cmd, dict):
        return jsonify({"error": "Invalid JSON payload."}), 400

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
        return jsonify({"error": "Invalid JSON payload."}), 400

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
# Command Results API (persist) - Revit -> Web
# =========================
@app.route("/api/projects/<project_id>/command-results", methods=["POST"])
def post_command_result(project_id):
    data = request.get_json(force=True)
    if not isinstance(data, dict):
        return jsonify({"error": "Invalid JSON payload."}), 400

    cmd_id = data.get("id")
    if not cmd_id:
        return jsonify({"error": "Missing id."}), 400

    data.setdefault("serverReceivedAt", datetime.utcnow().isoformat() + "Z")

    with _lock:
        _atomic_write_json(_cmd_result_path(project_id, cmd_id), data)

    return jsonify({"ok": True})

@app.route("/api/projects/<project_id>/command-results/<cmd_id>", methods=["GET"])
def get_command_result(project_id, cmd_id):
    path = _cmd_result_path(project_id, cmd_id)
    if not os.path.exists(path):
        return jsonify({"status": "pending"}), 200

    with _lock:
        data = _read_json(path, {"status": "pending"})
    return jsonify(data)


# =========================
# Upload snapshot (PNG/JPG) from Revit
# =========================
@app.route("/api/projects/<project_id>/snapshots/upload", methods=["POST"])
def upload_snapshot(project_id):
    if "file" not in request.files:
        return jsonify({"error": "No file field"}), 400

    f = request.files["file"]
    if not f or f.filename == "":
        return jsonify({"error": "Empty filename"}), 400

    view_uid = _safe(request.form.get("viewUniqueId", ""))
    ext = os.path.splitext(f.filename)[1].lower()
    if ext not in [".png", ".jpg", ".jpeg"]:
        ext = ".png"

    name = f"snap_{_safe(project_id)}_{view_uid}_{uuid.uuid4().hex}{ext}"
    save_path = os.path.join(UPLOAD_FOLDER, name)
    f.save(save_path)

    file_url = url_for("serve_uploads", filename=name, _external=True)
    return jsonify({"ok": True, "file": name, "url": file_url})


if __name__ == "__main__":
    # Fix "Khả năng B": no reloader, no debug multiprocess
    app.run(host="127.0.0.1", port=5000, debug=False, use_reloader=False)
