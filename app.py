import os
import json
import uuid
import traceback
from flask import Flask, request, render_template, jsonify, send_from_directory, url_for

app = Flask(__name__)
UPLOAD_FOLDER = 'uploads'
os.makedirs(UPLOAD_FOLDER, exist_ok=True)
app.config['UPLOAD_FOLDER'] = UPLOAD_FOLDER

# =========================
# UPLOAD API
# =========================
@app.route('/upload', methods=['POST'])
def upload_file():
    try:
        uploaded_filenames = []

        # 1. Nhận JSON raw (nếu có)
        if request.is_json:
            geojson_data = request.get_json()
            filename_json = f"model_{uuid.uuid4()}.json"
            output_path = os.path.join(app.config['UPLOAD_FOLDER'], filename_json)
            with open(output_path, 'w', encoding='utf-8') as f:
                json.dump(geojson_data, f, ensure_ascii=False, indent=2)
            uploaded_filenames.append(filename_json)

        # 2. Nhận File (GLB, JSON)
        elif 'file' in request.files:
            files = request.files.getlist('file')
            for file in files:
                if not file or file.filename == '':
                    continue
                ext = os.path.splitext(file.filename)[1].lower()
                filename = f"model_{uuid.uuid4()}{ext}"
                output_path = os.path.join(app.config['UPLOAD_FOLDER'], filename)
                file.save(output_path)
                uploaded_filenames.append(filename)
        else:
            raise ValueError("No valid file received.")

        # 3. Logic ưu tiên: Tìm GLB trước, nếu không có mới lấy JSON
        viewer_url = None
        
        glb_file = next((f for f in uploaded_filenames if f.endswith('.glb')), None)
        json_file = next((f for f in uploaded_filenames if f.endswith('.json')), None)

        if glb_file:
            viewer_url = url_for('viewer_glb', file=glb_file, _external=True)
        elif json_file:
            viewer_url = url_for('viewer_json', file=json_file, _external=True)

        if not viewer_url:
            raise ValueError("Upload thành công nhưng không tìm thấy file .glb hay .json để xem.")

        return jsonify({
            'viewer_url': viewer_url,
            'uploaded_files': uploaded_filenames
        })

    except Exception as e:
        traceback.print_exc()
        return jsonify({"error": str(e)}), 500

# =========================
# VIEWER ROUTES
# =========================
@app.route('/viewer_glb')
def viewer_glb():
    return render_template('viewer_glb.html')

@app.route('/viewer_json')
def viewer_json():
    return render_template('viewer_json.html')

@app.route('/data/<path:filename>')
def serve_data(filename):
    return send_from_directory(app.config['UPLOAD_FOLDER'], filename)

if __name__ == '__main__':
    app.run(debug=True, port=5000)