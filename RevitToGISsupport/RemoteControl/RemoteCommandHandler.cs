using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace RevitToGISsupport.RemoteControl
{

    public class IgnoreFailuresPreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            IList<FailureMessageAccessor> failures = failuresAccessor.GetFailureMessages();
            if (failures.Count == 0) return FailureProcessingResult.Continue;

            bool hasError = false;
            foreach (FailureMessageAccessor failure in failures)
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    failuresAccessor.DeleteWarning(failure);
                }
                else
                {
                    hasError = true;
                }
            }

            if (hasError)
            {
                return FailureProcessingResult.ProceedWithRollBack;
            }
            return FailureProcessingResult.Continue;
        }
    }

    public sealed class RemoteCommandHandler : IExternalEventHandler
    {
        private const string API_KEY = "CHANGE-ME-IN-PRODUCTION";

        public void Execute(UIApplication app)
        {
            Document targetDoc = null;
            if (!string.IsNullOrEmpty(RemoteSettings.TargetDocumentTitle))
            {
                foreach (Document d in app.Application.Documents)
                {
                    if (d.Title == RemoteSettings.TargetDocumentTitle)
                    {
                        targetDoc = d;
                        break;
                    }
                }
            }

            if (targetDoc == null) targetDoc = app?.ActiveUIDocument?.Document;
            if (targetDoc == null) return;

            UIDocument targetUiDoc = null;
            if (app.ActiveUIDocument?.Document?.Title == targetDoc.Title)
            {
                targetUiDoc = app.ActiveUIDocument;
            }

            while (RemoteCommandQueue.Items.TryDequeue(out var cmd))
            {
                if (cmd == null) continue;

                try
                {
                    switch (cmd.action)
                    {
                        case "activate_view":
                            TryActivateView(targetUiDoc, targetDoc, cmd);
                            break;

                        case "render_view_png":
                            TryRenderViewPng(targetDoc, cmd);
                            break;

                        case "export_view_glb":
                            TryExportViewGlb(targetDoc, cmd);
                            break;

                        case "update_parameter":
                            TryUpdateParameter(targetDoc, cmd);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    SendError(cmd, ex.Message);
                }
            }
        }

        private void TryActivateView(UIDocument uidoc, Document doc, RemoteCommand cmd)
        {
            if (uidoc == null) throw new Exception("Bạn phải đưa màn hình file này lên trên cùng trong Revit để xem View!");

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

            try
            {
                if (string.IsNullOrWhiteSpace(cmd.targetUniqueId))
                    throw new Exception("Missing targetUniqueId");

                if (cmd.parameters == null || cmd.parameters.Count == 0)
                    throw new Exception("No parameters provided to update.");

                using (Transaction t = new Transaction(doc, "Update from Web"))
                {
                    // [GẮN ỐNG GIẢM THANH VÀO GIAO DỊCH NÀY]
                    FailureHandlingOptions options = t.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(new IgnoreFailuresPreprocessor());
                    options.SetClearAfterRollback(true);
                    t.SetFailureHandlingOptions(options);

                    t.Start();
                    var elem = doc.GetElement(cmd.targetUniqueId);

                    if (elem == null)
                        throw new Exception($"Không tìm thấy đối tượng để sửa đổi!\nBạn đang mở file '{doc.Title}'. Hãy chắc chắn đây đúng là file gốc của dự án này.");

                    foreach (var kvp in cmd.parameters)
                    {
                        var paramName = kvp.Key;
                        var paramValStr = kvp.Value;

                        var param = elem.LookupParameter(paramName);
                        if (param == null) continue;
                        if (param.IsReadOnly) throw new Exception($"Parameter '{paramName}' is Read-Only.");

                        // Lớp phòng ngự 1: Chuẩn hóa Unicode và bỏ dấu cách thừa để check tên chính xác 100%
                        if (paramName.Equals("View Name", StringComparison.OrdinalIgnoreCase) ||
                            paramName.Equals("Sheet Name", StringComparison.OrdinalIgnoreCase) ||
                            paramName.Equals("Name", StringComparison.OrdinalIgnoreCase))
                        {
                            string currentName = (elem.Name ?? "").Normalize().Trim();
                            string targetName = (paramValStr ?? "").Normalize().Trim();
                            if (currentName.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                            {
                                continue; // Tên đã giống hệt, BỎ QUA NGAY
                            }
                        }

                        switch (param.StorageType)
                        {
                            case StorageType.String:
                                string currentStr = (param.AsString() ?? "").Normalize().Trim();
                                string targetStr = (paramValStr ?? "").Normalize().Trim();
                                if (!currentStr.Equals(targetStr, StringComparison.OrdinalIgnoreCase))
                                    param.Set(paramValStr);
                                break;

                            case StorageType.Double:
                                if (double.TryParse(paramValStr, out double dVal))
                                {
                                    if (Math.Abs(param.AsDouble() - dVal) > 0.0001)
                                        param.Set(dVal);
                                }
                                break;

                            case StorageType.Integer:
                                if (int.TryParse(paramValStr, out int iVal))
                                {
                                    if (param.AsInteger() != iVal)
                                        param.Set(iVal);
                                }
                                break;
                        }
                    }
                    t.Commit();
                }

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
            catch (Exception ex)
            {
                SendError(cmd, ex.Message);
            }
        }

        private void TryRenderViewPng(Document doc, RemoteCommand cmd)
        {
            var cmdId = cmd.id ?? Guid.NewGuid().ToString("N");
            var targetId = cmd.targetUniqueId;

            try
            {
                if (string.IsNullOrWhiteSpace(targetId)) throw new Exception("Missing targetUniqueId");
                var elem = doc.GetElement(targetId);
                if (!(elem is View v) || v.IsTemplate) throw new Exception("Khung nhìn (View) này không hợp lệ để xuất ảnh.");

                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports", "Snapshots");
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

                // --- DÙNG API NATIVE CỦA REVIT ĐỂ XUẤT ẢNH JPEG SIÊU NHẸ ---
                string baseName = "snap_" + Guid.NewGuid().ToString("N");
                string basePath = Path.Combine(folder, baseName);

                var imgOptions = new ImageExportOptions
                {
                    ZoomType = ZoomFitType.FitToPage,
                    PixelSize = cmd.pixelSize > 0 ? cmd.pixelSize : 800, // Đặt mặc định 800px là đủ nét
                    FilePath = basePath,
                    FitDirection = FitDirectionType.Horizontal,
                    HLRandWFViewsFileType = ImageFileType.JPEGLossless, // Đuôi JPEG
                    ShadowViewsFileType = ImageFileType.JPEGLossless,   // Đuôi JPEG
                    ImageResolution = ImageResolution.DPI_72,
                    ExportRange = ExportRange.SetOfViews
                };
                imgOptions.SetViewsAndSheets(new List<ElementId> { v.Id });

                // Lệnh cho Revit xả ảnh ra ổ cứng
                doc.ExportImage(imgOptions);

                // Vì Revit hay tự thêm tên View vào đuôi file (VD: snap_123 - 3D View - {3D}.jpg) 
                // nên ta phải tự động quét tên file thực tế vừa được tạo ra
                string actualFile = Directory.GetFiles(folder, baseName + "*.*").FirstOrDefault();

                if (string.IsNullOrWhiteSpace(actualFile) || !File.Exists(actualFile))
                    throw new Exception("Không thể tạo ảnh JPEG từ Revit.");
                // ------------------------------------------------------------------

                var baseUrl = GetBaseUrl();
                var projectId = RemoteSettings.ProjectId ?? "P001";

                Task.Run(async () =>
                {
                    try
                    {
                        // Đọc ảnh JPEG (Lúc này file chỉ còn khoảng 100-300KB)
                        byte[] imageBytes = File.ReadAllBytes(actualFile);
                        string base64String = Convert.ToBase64String(imageBytes);

                        // [QUAN TRỌNG] Đổi tiêu đề mã hóa sang dạng JPEG
                        string imageUrl = $"data:image/jpeg;base64,{base64String}";

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
                        await PostCommandResultAsync(baseUrl, projectId, new { id = cmdId, status = "error", message = "Lỗi xử lý ảnh: " + ex.Message });
                    }
                    finally
                    {
                        // Xóa file JPEG trên ổ cứng để đỡ chật máy
                        try { if (File.Exists(actualFile)) File.Delete(actualFile); } catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                SendError(cmd, ex.Message);
            }
        }

        private void TryExportViewGlb(Document doc, RemoteCommand cmd)
        {
            var cmdId = cmd.id ?? Guid.NewGuid().ToString("N");

            try
            {
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
            catch (Exception ex)
            {
                SendError(cmd, ex.Message);
            }
        }

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