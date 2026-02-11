using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;

namespace RevitToGISsupport.RemoteControl
{
    public sealed class RemoteCommandHandler : IExternalEventHandler
    {
        // QUAN TRỌNG: Key này phải khớp với log server "API_KEY: DEFAULT..."
        private const string API_KEY = "CHANGE-ME-IN-PRODUCTION";

        public void Execute(UIApplication app)
        {
            var uidoc = app?.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return;

            while (RemoteCommandQueue.Items.TryDequeue(out var cmd))
            {
                if (cmd == null) continue;

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

                // Gửi báo cáo thành công về Server
                PostCommandResult(RemoteSettings.ServerBaseUrl, RemoteSettings.ProjectId, new
                {
                    id = cmdId,
                    status = "done",
                    message = "Update successful",
                    updatedFields = cmd.parameters.Keys.ToList()
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
            try
            {
                if (string.IsNullOrWhiteSpace(cmd.targetUniqueId)) throw new Exception("Missing targetUniqueId");
                var elem = doc.GetElement(cmd.targetUniqueId);
                if (!(elem is View v) || v.IsTemplate) throw new Exception("Target is not a valid View");

                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports", "Snapshots");
                var pngPath = ViewImageExporter.ExportPng(doc, v, folder, cmd.pixelSize);

                if (string.IsNullOrWhiteSpace(pngPath) || !File.Exists(pngPath))
                    throw new Exception("PNG export failed");

                var imageUrl = UploadSnapshot(RemoteSettings.ServerBaseUrl, RemoteSettings.ProjectId, cmd.targetUniqueId, pngPath);

                PostCommandResult(RemoteSettings.ServerBaseUrl, RemoteSettings.ProjectId, new
                {
                    id = cmdId,
                    status = "done",
                    imageUrl = imageUrl,
                    viewUniqueId = cmd.targetUniqueId
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
                // Giả sử hàm ExportGlbForView đã có
                var glbPath = RemoteGlbExporter.ExportGlbForView(doc, v3, folder);

                PostCommandResult(RemoteSettings.ServerBaseUrl, RemoteSettings.ProjectId, new
                {
                    id = cmdId,
                    status = "done",
                    message = "GLB Exported locally"
                });
            }
            catch (Exception ex)
            {
                SendError(cmd, ex.Message);
            }
        }

        // --- HELPERS (ĐÃ THÊM API KEY) ---

        private void SendError(RemoteCommand cmd, string msg)
        {
            var cmdId = cmd?.id ?? "unknown";
            try
            {
                PostCommandResult(RemoteSettings.ServerBaseUrl, RemoteSettings.ProjectId, new
                {
                    id = cmdId,
                    status = "error",
                    message = msg
                });
            }
            catch { }
        }

        private string UploadSnapshot(string baseUrl, string projectId, string viewUniqueId, string pngPath)
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
            using (var form = new MultipartFormDataContent())
            {
                // [FIX] THÊM HEADER API KEY
                http.DefaultRequestHeaders.Add("X-API-Key", API_KEY);

                form.Add(new StringContent(viewUniqueId ?? ""), "viewUniqueId");
                form.Add(new StreamContent(File.OpenRead(pngPath)), "file", Path.GetFileName(pngPath));

                var url = $"{baseUrl.TrimEnd('/')}/api/projects/{Uri.EscapeDataString(projectId)}/snapshots/upload";

                var res = http.PostAsync(url, form).GetAwaiter().GetResult();
                var body = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (!res.IsSuccessStatusCode) throw new Exception("Upload failed: " + body);

                dynamic obj = JsonConvert.DeserializeObject(body);
                return (string)obj?.url;
            }
        }

        private void PostCommandResult(string baseUrl, string projectId, object payload)
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                var url = $"{baseUrl.TrimEnd('/')}/api/projects/{Uri.EscapeDataString(projectId)}/command-results";
                var json = JsonConvert.SerializeObject(payload);

                // [FIX] THÊM HEADER API KEY
                http.DefaultRequestHeaders.Add("X-API-Key", API_KEY);

                http.PostAsync(url, new StringContent(json, System.Text.Encoding.UTF8, "application/json"))
                    .GetAwaiter().GetResult();
            }
        }

        public string GetName() => "RemoteCommandHandler";
    }
}