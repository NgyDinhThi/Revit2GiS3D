using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows; // Sử dụng bảng thông báo gốc của Windows

namespace RevitToGISsupport.RemoteControl
{
    public sealed class RemoteCommandHandler : IExternalEventHandler
    {
        private const string API_KEY = "CHANGE-ME-IN-PRODUCTION";

        public void Execute(UIApplication app)
        {
            var uidoc = app?.ActiveUIDocument;
            var doc = uidoc?.Document;

            if (doc == null)
            {
                // Nếu Revit không nhận diện được file đang mở
                return;
            }

            while (RemoteCommandQueue.Items.TryDequeue(out var cmd))
            {
                if (cmd == null) continue;

                // [MÁY DÒ MÌN SỐ 1] - Báo cáo ngay khi chộp được lệnh
                MessageBox.Show($"1. Đã chộp được lệnh từ Web: {cmd.action}", "BIM Sync Debug", MessageBoxButton.OK, MessageBoxImage.Information);

                try
                {
                    switch (cmd.action)
                    {
                        case "activate_view":
                            TryActivateView(uidoc, doc, cmd);
                            break;

                        case "render_view_png":
                            TryRenderViewPng(doc, cmd);
                            break;

                        case "export_view_glb":
                            TryExportViewGlb(doc, cmd);
                            break;

                        case "update_parameter":
                            TryUpdateParameter(doc, cmd);
                            break;

                        default:
                            MessageBox.Show($"Lệnh lạ không xác định: {cmd.action}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"LỖI NẶNG KHI CHẠY LỆNH {cmd.action}:\n{ex.Message}", "BIM Sync Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                    SendError(cmd, ex.Message);
                }
            }
        }

        private void TryActivateView(UIDocument uidoc, Document doc, RemoteCommand cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd.targetUniqueId)) return;
            var elem = doc.GetElement(cmd.targetUniqueId);
            if (elem is View v && !v.IsTemplate)
            {
                try { uidoc.RequestViewChange(v); } catch { }
            }
        }

        private void TryUpdateParameter(Document doc, RemoteCommand cmd)
        {
            var cmdId = cmd.id ?? Guid.NewGuid().ToString("N");

            if (string.IsNullOrWhiteSpace(cmd.targetUniqueId))
                throw new Exception("Missing targetUniqueId");

            if (cmd.parameters == null || cmd.parameters.Count == 0)
                throw new Exception("No parameters provided to update.");

            using (Transaction t = new Transaction(doc, "Update from Web"))
            {
                t.Start();
                var elem = doc.GetElement(cmd.targetUniqueId);
                if (elem == null) throw new Exception("Element not found in Revit.");

                foreach (var kvp in cmd.parameters)
                {
                    var paramName = kvp.Key;
                    var paramValStr = kvp.Value;

                    var param = elem.LookupParameter(paramName);
                    if (param == null) continue;
                    if (param.IsReadOnly) throw new Exception($"Parameter '{paramName}' is Read-Only.");

                    switch (param.StorageType)
                    {
                        case StorageType.String:
                            param.Set(paramValStr);
                            break;
                        case StorageType.Double:
                            if (double.TryParse(paramValStr, out double dVal)) param.Set(dVal);
                            break;
                        case StorageType.Integer:
                            if (int.TryParse(paramValStr, out int iVal)) param.Set(iVal);
                            break;
                    }
                }
                t.Commit();
            }

            MessageBox.Show("2. Đổi Parameter thành công! Bắt đầu gửi báo cáo về Web...", "BIM Sync Debug");

            var fields = cmd.parameters.Keys.ToList();
            Task.Run(async () =>
            {
                await PostCommandResultAsync(GetBaseUrl(), RemoteSettings.ProjectId, new
                {
                    id = cmdId,
                    status = "done",
                    message = "Update successful",
                    updatedFields = fields
                });
            });
        }

        private void TryRenderViewPng(Document doc, RemoteCommand cmd)
        {
            var cmdId = cmd.id ?? Guid.NewGuid().ToString("N");
            var targetId = cmd.targetUniqueId;

            if (string.IsNullOrWhiteSpace(targetId)) throw new Exception("Missing targetUniqueId");
            var elem = doc.GetElement(targetId);
            if (!(elem is View v) || v.IsTemplate) throw new Exception("Khung nhìn (View) này không hợp lệ để xuất ảnh.");

            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports", "Snapshots");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

            var pngPath = ViewImageExporter.ExportPng(doc, v, folder, cmd.pixelSize > 0 ? cmd.pixelSize : 1000);

            if (string.IsNullOrWhiteSpace(pngPath) || !File.Exists(pngPath))
                throw new Exception($"Không thể lưu ảnh ra máy tính tại:\n{pngPath}");

            // [MÁY DÒ MÌN SỐ 2]
            MessageBox.Show($"2. Đã chụp ảnh xong!\nLưu tại: {pngPath}\nBắt đầu Upload lên Server...", "BIM Sync Debug", MessageBoxButton.OK, MessageBoxImage.Information);

            var baseUrl = GetBaseUrl();
            var projectId = RemoteSettings.ProjectId ?? "P001";

            Task.Run(async () =>
            {
                try
                {
                    var imageUrl = await UploadSnapshotAsync(baseUrl, projectId, targetId, pngPath);
                    await PostCommandResultAsync(baseUrl, projectId, new
                    {
                        id = cmdId,
                        status = "done",
                        imageUrl = imageUrl,
                        viewUniqueId = targetId
                    });
                }
                catch (Exception ex)
                {
                    // Lỗi mạng sẽ được bắn vào log Server
                    await PostCommandResultAsync(baseUrl, projectId, new { id = cmdId, status = "error", message = "Lỗi Upload: " + ex.Message });
                }
            });
        }

        private void TryExportViewGlb(Document doc, RemoteCommand cmd)
        {
            var cmdId = cmd.id ?? Guid.NewGuid().ToString("N");

            if (string.IsNullOrWhiteSpace(cmd.targetUniqueId)) throw new Exception("Missing targetUniqueId");
            var elem = doc.GetElement(cmd.targetUniqueId);
            if (!(elem is View3D v3)) throw new Exception("Target is not a 3D View");

            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports", "Glb");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

            var glbPath = RemoteGlbExporter.ExportGlbForView(doc, v3, folder);

            Task.Run(async () =>
            {
                await PostCommandResultAsync(GetBaseUrl(), RemoteSettings.ProjectId, new
                {
                    id = cmdId,
                    status = "done",
                    message = "GLB Exported locally"
                });
            });
        }

        // =========================================================
        // HELPERS: XỬ LÝ MẠNG TÁCH BIỆT HOÀN TOÀN KHỎI REVIT
        // =========================================================

        private string GetBaseUrl()
        {
            var url = RemoteSettings.ServerBaseUrl;
            return string.IsNullOrWhiteSpace(url) ? "http://127.0.0.1:5000" : url;
        }

        private void SendError(RemoteCommand cmd, string msg)
        {
            var cmdId = cmd?.id ?? "unknown";
            var baseUrl = GetBaseUrl();
            var projectId = RemoteSettings.ProjectId ?? "P001";

            Task.Run(async () =>
            {
                await PostCommandResultAsync(baseUrl, projectId, new { id = cmdId, status = "error", message = msg });
            });
        }

        private async Task<string> UploadSnapshotAsync(string baseUrl, string projectId, string viewUniqueId, string pngPath)
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
            using (var form = new MultipartFormDataContent())
            {
                http.DefaultRequestHeaders.Add("X-API-Key", API_KEY);

                form.Add(new StringContent(viewUniqueId ?? ""), "viewUniqueId");
                form.Add(new StreamContent(File.OpenRead(pngPath)), "file", Path.GetFileName(pngPath));

                var url = $"{baseUrl.TrimEnd('/')}/api/projects/{Uri.EscapeDataString(projectId)}/snapshots/upload";

                var res = await http.PostAsync(url, form).ConfigureAwait(false);
                var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!res.IsSuccessStatusCode) throw new Exception("Upload failed: " + body);

                var obj = JObject.Parse(body);
                return obj["url"]?.ToString();
            }
        }

        private async Task PostCommandResultAsync(string baseUrl, string projectId, object payload)
        {
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    var url = $"{baseUrl.TrimEnd('/')}/api/projects/{Uri.EscapeDataString(projectId)}/command-results";
                    var json = JsonConvert.SerializeObject(payload);

                    http.DefaultRequestHeaders.Add("X-API-Key", API_KEY);
                    await http.PostAsync(url, new StringContent(json, System.Text.Encoding.UTF8, "application/json")).ConfigureAwait(false);
                }
            }
            catch { }
        }

        public string GetName() => "RemoteCommandHandler";
    }
}