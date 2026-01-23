using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net.Http;

namespace RevitToGISsupport.RemoteControl
{
    public sealed class RemoteCommandHandler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            var uidoc = app?.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return;

            while (RemoteCommandQueue.Items.TryDequeue(out var cmd))
            {
                if (cmd == null) continue;

                if (cmd.action == "activate_view")
                {
                    TryActivateView(uidoc, doc, cmd);
                    continue;
                }

                if (cmd.action == "render_view_png")
                {
                    TryRenderViewPng(doc, cmd);
                    continue;
                }

                if (cmd.action == "export_view_glb")
                {
                    TryExportViewGlb(doc, cmd);
                    continue;
                }

                if (cmd.action == "export_project_glb")
                {
                    TryExportProjectGlb(doc, cmd);
                    continue;
                }
            }
        }

        private static void TryActivateView(UIDocument uidoc, Document doc, RemoteCommand cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd.targetUniqueId)) return;

            var elem = doc.GetElement(cmd.targetUniqueId);
            if (elem is View v && !v.IsTemplate)
            {
                try { uidoc.RequestViewChange(v); } catch { }
            }
        }

        // Bạn đã có render PNG hoạt động rồi; giữ nguyên logic upload snapshot + post result
        private static void TryRenderViewPng(Document doc, RemoteCommand cmd)
        {
            var cmdId = cmd.id ?? Guid.NewGuid().ToString("N");

            try
            {
                if (string.IsNullOrWhiteSpace(cmd.targetUniqueId))
                    throw new Exception("Missing targetUniqueId");

                var elem = doc.GetElement(cmd.targetUniqueId);
                if (!(elem is View v) || v.IsTemplate)
                    throw new Exception("Target is not a valid View");

                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports", "Snapshots");
                Directory.CreateDirectory(folder);

                var pngPath = ViewImageExporter.ExportPng(doc, v, folder, cmd.pixelSize);
                if (string.IsNullOrWhiteSpace(pngPath) || !File.Exists(pngPath))
                    throw new Exception("PNG export failed");

                var imageUrl = UploadSnapshot(RemoteSettings.ServerBaseUrl, RemoteSettings.ProjectId, cmd.targetUniqueId, pngPath);

                PostCommandResult(RemoteSettings.ServerBaseUrl, RemoteSettings.ProjectId, new
                {
                    id = cmdId,
                    status = "done",
                    imageUrl = imageUrl,
                    viewUniqueId = cmd.targetUniqueId,
                    createdAt = DateTimeOffset.Now.ToString("o")
                });
            }
            catch (Exception ex)
            {
                PostCommandResult(RemoteSettings.ServerBaseUrl, RemoteSettings.ProjectId, new
                {
                    id = cmdId,
                    status = "error",
                    message = ex.Message,
                    viewUniqueId = cmd.targetUniqueId
                });
            }
        }

        private static void TryExportViewGlb(Document doc, RemoteCommand cmd)
        {
            var cmdId = cmd.id ?? Guid.NewGuid().ToString("N");

            try
            {
                if (string.IsNullOrWhiteSpace(cmd.targetUniqueId))
                    throw new Exception("Missing targetUniqueId");

                var elem = doc.GetElement(cmd.targetUniqueId);
                if (!(elem is View3D v3) || v3.IsTemplate)
                    throw new Exception("Target is not a valid View3D");

                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports", "Glb");
                var glbPath = RemoteGlbExporter.ExportGlbForView(doc, v3, folder);

                var upload = UploadGlb(RemoteSettings.ServerBaseUrl, RemoteSettings.ProjectId, cmd.targetUniqueId, glbPath);

                PostCommandResult(RemoteSettings.ServerBaseUrl, RemoteSettings.ProjectId, new
                {
                    id = cmdId,
                    status = "done",
                    glbFile = upload.glbFile,
                    viewerUrl = upload.viewerUrl,
                    glbUrl = upload.glbUrl,
                    viewUniqueId = cmd.targetUniqueId,
                    createdAt = DateTimeOffset.Now.ToString("o")
                });
            }
            catch (Exception ex)
            {
                PostCommandResult(RemoteSettings.ServerBaseUrl, RemoteSettings.ProjectId, new
                {
                    id = cmdId,
                    status = "error",
                    message = ex.Message,
                    viewUniqueId = cmd.targetUniqueId
                });
            }
        }

        private static void TryExportProjectGlb(Document doc, RemoteCommand cmd)
        {
            var cmdId = cmd.id ?? Guid.NewGuid().ToString("N");

            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports", "Glb");
                var glbPath = RemoteGlbExporter.ExportGlbForProject(doc, folder);

                var upload = UploadGlb(RemoteSettings.ServerBaseUrl, RemoteSettings.ProjectId, null, glbPath);

                PostCommandResult(RemoteSettings.ServerBaseUrl, RemoteSettings.ProjectId, new
                {
                    id = cmdId,
                    status = "done",
                    glbFile = upload.glbFile,
                    viewerUrl = upload.viewerUrl,
                    glbUrl = upload.glbUrl,
                    createdAt = DateTimeOffset.Now.ToString("o")
                });
            }
            catch (Exception ex)
            {
                PostCommandResult(RemoteSettings.ServerBaseUrl, RemoteSettings.ProjectId, new
                {
                    id = cmdId,
                    status = "error",
                    message = ex.Message
                });
            }
        }

        private static (string glbFile, string viewerUrl, string glbUrl) UploadGlb(string baseUrl, string projectId, string viewUniqueId, string glbPath)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var form = new MultipartFormDataContent();

            if (!string.IsNullOrWhiteSpace(projectId))
                form.Add(new StringContent(projectId), "projectId");

            if (!string.IsNullOrWhiteSpace(viewUniqueId))
                form.Add(new StringContent(viewUniqueId), "viewUniqueId");

            form.Add(new StreamContent(File.OpenRead(glbPath)), "file", Path.GetFileName(glbPath));

            var url = $"{baseUrl.TrimEnd('/')}/upload";
            var res = http.PostAsync(url, form).GetAwaiter().GetResult();
            var body = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!res.IsSuccessStatusCode)
                throw new Exception("Upload GLB failed: " + body);

            var jo = JObject.Parse(body);
            var viewerUrl = jo["viewer_url"]?.ToString() ?? jo["viewerUrl"]?.ToString() ?? "";
            var uploaded = jo["uploaded"] as JArray;
            string glbFile = "";
            if (uploaded != null)
            {
                foreach (var x in uploaded)
                {
                    var s = x?.ToString() ?? "";
                    if (s.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
                    {
                        glbFile = s;
                        break;
                    }
                }
            }

            var glbUrl = !string.IsNullOrWhiteSpace(glbFile)
                ? $"{baseUrl.TrimEnd('/')}/uploads/{Uri.EscapeDataString(glbFile)}"
                : "";

            return (glbFile, viewerUrl, glbUrl);
        }

        private static string UploadSnapshot(string baseUrl, string projectId, string viewUniqueId, string pngPath)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var form = new MultipartFormDataContent();

            form.Add(new StringContent(viewUniqueId ?? ""), "viewUniqueId");
            form.Add(new StreamContent(File.OpenRead(pngPath)), "file", Path.GetFileName(pngPath));

            var url = $"{baseUrl.TrimEnd('/')}/api/projects/{Uri.EscapeDataString(projectId)}/snapshots/upload";
            var res = http.PostAsync(url, form).GetAwaiter().GetResult();
            var body = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!res.IsSuccessStatusCode)
                throw new Exception("Upload snapshot failed: " + body);

            dynamic obj = JsonConvert.DeserializeObject(body);
            string imageUrl = (string)obj?.url;
            if (string.IsNullOrWhiteSpace(imageUrl))
                throw new Exception("Server did not return image url");

            return imageUrl;
        }

        private static void PostCommandResult(string baseUrl, string projectId, object payload)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var url = $"{baseUrl.TrimEnd('/')}/api/projects/{Uri.EscapeDataString(projectId)}/command-results";
            var json = JsonConvert.SerializeObject(payload);
            http.PostAsync(url, new StringContent(json, System.Text.Encoding.UTF8, "application/json"))
                .GetAwaiter().GetResult();
        }

        public string GetName() => "RemoteCommandHandler";
    }
}
