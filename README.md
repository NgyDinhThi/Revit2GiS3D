<div align="center">

# BIM Sync Manager
<br/>

> Xem trước mô hình 3D, ra lệnh render ảnh và chỉnh sửa thông số bản vẽ Revit  
> từ xa — **không cần cài đặt Revit trên máy cá nhân.**

</div> 
---

##  Tính năng nổi bật
| Tính năng | Mô tả |
|-----------|-------|
|  **Đồng bộ 3D chuẩn xác** | Tự động trích xuất mô hình 3D sang định dạng `.glb`  |
|  **Tương tác hai chiều** | Nhận lệnh từ Web, xử lý ngầm trong Revit và trả kết quả ngược về Web |
|  **Render từ xa** | Ra lệnh cho Revit tự động chụp ảnh khung nhìn (View) sang JPEG và gửi ngay lên Web |
|  **Cập nhật thời gian thực** | Socket.IO đảm bảo mọi thay đổi hiển thị tức thì trên trình duyệt |

---

##  Kiến trúc hệ thống

Hệ thống hoạt động theo mô hình **Client → Server → Client** với 3 thành phần chính:

```
┌─────────────────────┐        ┌──────────────────────┐        ┌─────────────────────┐
│                     │        │                      │        │                     │
│     C# ADD-IN       │◄──────►│    PYTHON SERVER     │◄──────►│      WEB CLIENT     │
│   (Revit Plugin)    │  REST  │  (Flask + Socket.IO) │  WS    │     (HTML / JS)     │
│                     │        │                      │        │                     │
└─────────────────────┘        └──────────────────────┘        └─────────────────────┘
        │                               │                               │
   Quét & Xuất                   Hàng đợi lệnh                   Xem 3D / Render  
   GLB + JSON                   Command Queue                 
```

---

## Luồng hoạt động (4 Giai đoạn)

<details>
<summary><b>Giai đoạn 1 — Khởi tạo & Đồng bộ dữ liệu (Revit → Web)</b></summary>

<br/>

1. **Khởi động:** Người dùng mở Revit, chọn file, vào Add-Ins, chọn External Tools và nhấn nút **BIM-REVIT** trên thanh công cụ để mở Control Panel.
2. **Quét dữ liệu:** `BrowserTreeBuilder` duyệt toàn bộ cây thư mục (Views, Sheets, Schedules) và lấy `UniqueId` của từng phần tử.
3. **Trích xuất 3D:** `RemoteGlbExporter` sử dụng Revit Native API để:
   - Thu thập hình học & chuyển đổi đơn vị sang hệ Mét
   - Thu thập màu sắc vật liệu chuẩn
   - Đóng gói thành file `.glb`
4. **Tải lên Server:** Đóng gói cây cấu trúc (JSON) + mô hình 3D (GLB) → gửi qua RESTful API.

</details>

<details>
<summary><b>Giai đoạn 2 — Trải nghiệm & Ra lệnh từ xa (Web → Server)</b></summary>

<br/>

1. **Giao diện Web:** Khách hàng / Quản lý truy cập link Web (qua Ngrok). Cây thư mục và mô hình 3D được tự động tải về trình duyệt.
2. **Hiển thị 3D:** JavaScript ngầm khóa vật liệu `OPAQUE` và tinh chỉnh ánh sáng để hiển thị khối kiến trúc sắc nét nhất.
3. **Phát lệnh từ Web:**
   - **Cập nhật tham số:** Sửa tên bản vẽ/View → Bấm *"Đẩy lên Revit"* (`update_parameter`)
   - **Yêu cầu Render:** Chọn View → Bấm *"Render ảnh"* (`render_view_png`)
4. **Hàng đợi:** Các lệnh được lưu tạm thời vào **Command Queue** trên Server.

</details>

<details>
<summary><b>Giai đoạn 3 — Lắng nghe & Xử lý ngầm (Server → Revit)</b></summary>

<br/>

1. **Polling:** `RemoteCommandPoller` chạy ngầm trong Revit, liên tục gửi yêu cầu tới Server để kiểm tra lệnh mới.
2. **Xác nhận :** Khi nhận lệnh, Revit gửi tín hiệu Ack để Server xóa lệnh khỏi hàng đợi — đảm bảo không thực thi lặp.
3. **Thực thi:**
   - Khởi tạo Transaction ngầm trong Revit
   - `IgnoreFailuresPreprocessor` tự động chặn mọi popup cảnh báo rác
   - Chạy lệnh cập nhật tham số hoặc xuất ảnh JPEG siêu nhẹ

</details>

<details>
<summary><b>Giai đoạn 4 — Cập nhật thời gian thực (Revit → Server → Web)</b></summary>

<br/>

1. **Trả kết quả:** Revit đóng gói kết quả (báo cáo thành công hoặc mã Base64 của ảnh JPEG) và POST ngược lên Server.
2. **Phát sóng:** Server dùng **WebSockets (Socket.IO)** bắn thông báo tới tất cả trình duyệt đang kết nối.
3. **Hiển thị:** Giao diện Web nhận tín hiệu, tắt trạng thái *"Processing..."*, ảnh Render hoặc tên thông số xuất hiện ngay — **chỉ trong 1–2 giây**.

</details>

---

## Cài đặt & Chạy dịch vụ

### Yêu cầu hệ thống

| Thành phần | Yêu cầu |
|------------|---------|
| Python | 3.x trở lên |
| Autodesk Revit | Phiên bản có hỗ trợ Add-in API |
| .NET / C# | Để build Revit Add-in |

###  Khởi động Server

```bash
# 1. Cài đặt thư viện
pip install -r requirements.txt

# 2. Khởi động máy chủ
python app.py
```

###  Thư viện Python

```
Flask
Flask-SocketIO
Flask-CORS
requests
```

---

##  Cấu trúc dự án

```
REVITTOGIS/
├──  static/                    # Tài nguyên tĩnh
│   ├──  geojson/               # Dữ liệu GeoJSON
│   ├──  models/                # Mô hình 3D tĩnh
│   ├──  browser.html           # Giao diện duyệt cây thư mục Revit
│   ├──  historyWindow.html     # Cửa sổ lịch sử lệnh
│   └──  style.css              # Stylesheet chung
├──  templates/                 # Template HTML (Flask Jinja2)
│   └──  viewer_glb.html        # Trình xem mô hình 3D (.glb)
├──  uploads/                   # File tải lên từ Revit Add-in
├──  app.py                     # Điểm khởi động Flask Server
├──  upload_log.txt             # Log file tải lên
├──  ngrok_recovery_codes.txt   # Mã khôi phục Ngrok tunnel
└──  .gitignore
```



