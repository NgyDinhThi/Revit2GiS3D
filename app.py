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

        # --- Trường hợp nhận dữ liệu JSON raw ---
        if request.is_json:
            geojson_data = request.get_json()
            filename_json = f"model_{uuid.uuid4()}.json"
            output_path = os.path.join(app.config['UPLOAD_FOLDER'], filename_json)
            with open(output_path, 'w', encoding='utf-8') as f:
                json.dump(geojson_data, f, ensure_ascii=False, indent=2)
            uploaded_filenames.append(filename_json)
            print(f"✅ Saved JSON: {filename_json}")

        # --- Trường hợp nhận nhiều file (GLB + JSON) ---
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
                print(f"✅ Saved file: {filename}")
        else:
            raise ValueError("No valid file or JSON data received.")

        # --- Xác định file viewer chính ---
        viewer_url = None
        for fname in uploaded_filenames:
            if fname.endswith('.json'):
                viewer_url = url_for('viewer_json', file=fname, _external=True)
                break
            elif fname.endswith('.glb'):
                viewer_url = url_for('viewer_glb', file=fname, _external=True)

        if not viewer_url:
            raise ValueError("No supported file (.glb or .json) found in upload.")

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
