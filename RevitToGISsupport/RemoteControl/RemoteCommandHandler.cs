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
                // Xóa bỏ các cảnh báo (Warnings) gây treo lệnh đổi tên View
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    failuresAccessor.DeleteWarning(failure);
                }
                else
                {
                    hasError = true;
                }
            }

            return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }

    public sealed class RemoteCommandHandler : IExternalEventHandler
    {
        private const string API_KEY = "CHANGE-ME-IN-PRODUCTION";
        public static event Action<string, bool> OnExecutionFinished;

        public void Execute(UIApplication app)
        {
            Document targetDoc = app?.ActiveUIDocument?.Document;
            if (targetDoc == null)
            {
                OnExecutionFinished?.Invoke("Lỗi: Không tìm thấy Document hoạt động.", false);
                return;
            }

            UIDocument targetUiDoc = app.ActiveUIDocument;
            bool hasProcessed = false;

            while (RemoteCommandQueue.Items.TryDequeue(out var cmd))
            {
                if (cmd == null) continue;

                // BẮT BUỘC PHẢI CÓ DÒNG NÀY ĐỂ XÁC NHẬN LÀ CÓ LỆNH ĐƯỢC CHẠY
                hasProcessed = true;

                try
                {
                    switch (cmd.action)
                    {
                        case "activate_view": TryActivateView(targetUiDoc, targetDoc, cmd); break;
                        case "render_view_png": TryRenderViewPng(targetDoc, cmd); break;
                        case "export_view_glb": TryExportViewGlb(targetDoc, cmd); break;
                        case "update_parameter": TryUpdateParameter(targetUiDoc, targetDoc, cmd); break;
                    }
                }
                catch (Exception ex)
                {
                    SendError(cmd, ex.Message);
                }
            }
            // SAU KHI KẾT THÚC VÒNG LẶP WHILE
            if (hasProcessed)
            {
                OnExecutionFinished?.Invoke("Áp dụng thành công! Vui lòng kiểm tra lại mô hình.", true);
            }
        }

        private void TryUpdateParameter(UIDocument uidoc, Document doc, RemoteCommand cmd)
        {
            var cmdId = cmd.id ?? Guid.NewGuid().ToString("N");

            try
            {
                if (string.IsNullOrWhiteSpace(cmd.targetUniqueId)) throw new Exception("Missing targetUniqueId");

                using (Transaction t = new Transaction(doc, "Update from Web"))
                {
                    FailureHandlingOptions options = t.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(new IgnoreFailuresPreprocessor());
                    t.SetFailureHandlingOptions(options);

                    t.Start();
                    var elem = doc.GetElement(cmd.targetUniqueId);
                    if (elem == null) throw new Exception("Không tìm thấy đối tượng trong file Revit hiện tại.");

                    foreach (var kvp in cmd.parameters)
                    {
                        var param = elem.LookupParameter(kvp.Key);
                        if (param == null || param.IsReadOnly) continue;

                        string newVal = kvp.Value?.Normalize().Trim();

                        // Xử lý logic đổi tên đặc biệt cho View/Sheet
                        if (kvp.Key.Contains("Name") && (elem is View || elem is ViewSheet))
                        {
                            if (elem.Name.Normalize().Trim().Equals(newVal, StringComparison.OrdinalIgnoreCase)) continue;
                        }

                        switch (param.StorageType)
                        {
                            case StorageType.String:
                                param.Set(newVal);
                                break;
                            case StorageType.Double:
                                if (double.TryParse(newVal, out double d)) param.Set(d);
                                break;
                            case StorageType.Integer:
                                if (int.TryParse(newVal, out int i)) param.Set(i);
                                break;
                        }
                    }
                    t.Commit();
                }
                uidoc?.RefreshActiveView();
                Task.Run(async () => {
                    await PostCommandResultAsync(GetBaseUrl(), RemoteSettings.ProjectId, new
                    {
                        id = cmdId,
                        status = "done",
                        message = "Update & Auto-Saved successful"
                    });
                });
            }
            catch (Exception ex) { SendError(cmd, ex.Message); }
        }

        // --- CÁC HÀM HỖ TRỢ (GIỮ NGUYÊN LOGIC CŨ) ---
        private void TryActivateView(UIDocument uidoc, Document doc, RemoteCommand cmd)
        {
            if (uidoc == null) throw new Exception("Revit window must be active.");
            var elem = doc.GetElement(cmd.targetUniqueId);
            if (elem is View v && !v.IsTemplate) uidoc.RequestViewChange(v);
        }

        private void TryRenderViewPng(Document doc, RemoteCommand cmd)
        {
            var cmdId = cmd.id ?? Guid.NewGuid().ToString("N");
            var elem = doc.GetElement(cmd.targetUniqueId);
            if (!(elem is View v)) return;

            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports", "Snapshots");
            Directory.CreateDirectory(folder);
            string basePath = Path.Combine(folder, "snap_" + Guid.NewGuid().ToString("N"));

            var imgOptions = new ImageExportOptions
            {
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = cmd.pixelSize > 0 ? cmd.pixelSize : 1000,
                FilePath = basePath,
                FitDirection = FitDirectionType.Horizontal,
                HLRandWFViewsFileType = ImageFileType.JPEGLossless,
                ImageResolution = ImageResolution.DPI_72,
                ExportRange = ExportRange.SetOfViews
            };
            imgOptions.SetViewsAndSheets(new List<ElementId> { v.Id });
            doc.ExportImage(imgOptions);

            string actualFile = Directory.GetFiles(folder, Path.GetFileName(basePath) + "*.*").FirstOrDefault();
            if (actualFile == null) return;

            Task.Run(async () => {
                byte[] bytes = File.ReadAllBytes(actualFile);
                await PostCommandResultAsync(GetBaseUrl(), RemoteSettings.ProjectId, new
                {
                    id = cmdId,
                    status = "done",
                    imageUrl = $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}"
                });
                File.Delete(actualFile);
            });
        }

        private void TryExportViewGlb(Document doc, RemoteCommand cmd)
        {
            var elem = doc.GetElement(cmd.targetUniqueId);
            if (elem is View3D v3)
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports", "Glb");
                RemoteGlbExporter.ExportGlbForView(doc, v3, folder);
            }
        }

        private string GetBaseUrl() => RemoteSettings.ServerBaseUrl ?? "http://127.0.0.1:5000";

        private void SendError(RemoteCommand cmd, string msg)
        {
            Task.Run(async () => await PostCommandResultAsync(GetBaseUrl(), RemoteSettings.ProjectId, new { id = cmd?.id, status = "error", message = msg }));
        }

        private async Task PostCommandResultAsync(string baseUrl, string projectId, object payload)
        {
            try
            {
                using (var http = new HttpClient())
                {
                    http.DefaultRequestHeaders.Add("X-API-Key", API_KEY);
                    var json = JsonConvert.SerializeObject(payload);
                    await http.PostAsync($"{baseUrl.TrimEnd('/')}/api/projects/{projectId}/command-results", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
                }
            }
            catch { }
        }

        public string GetName() => "RemoteCommandHandler";
    }
}