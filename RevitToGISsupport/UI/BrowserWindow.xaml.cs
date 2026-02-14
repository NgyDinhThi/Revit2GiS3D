using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitToGISsupport.DataTree;
using RevitToGISsupport.Models;    
using RevitToGISsupport.Services; 
using RevitToGISsupport.RemoteControl;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RevitToGISsupport.UI
{
    public partial class BrowserWindow : Window
    {
        private readonly UIApplication _uiapp;
        private readonly Document _doc;
        private readonly ExternalEvent _activateEvent;

        private BrowserNode _root;

        // API Key (Phải khớp với Server app.py)
        private const string API_KEY = "CHANGE-ME-IN-PRODUCTION";

        private static readonly HttpClient SharedHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        private CancellationTokenSource _publishCts;

        // Remote Control
        private static RemoteCommandHandler _remoteHandler;
        private static ExternalEvent _remoteEvent;
        private RemoteCommandPoller _poller;

        private string _pollerServer;
        private string _pollerProjectId;
        private string _pollerClientId;

        public BrowserWindow(UIApplication uiapp, ExternalEvent activateEvent)
        {
            InitializeComponent();

            _uiapp = uiapp;
            _doc = uiapp?.ActiveUIDocument?.Document;
            _activateEvent = activateEvent;

            Loaded += BrowserWindow_Loaded;
            Closed += BrowserWindow_Closed;
        }

        private void BrowserWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_doc == null)
            {
                MessageBox.Show("Không lấy được Document.", "Browser", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
                return;
            }

            if (string.IsNullOrWhiteSpace(tbServer.Text))
                tbServer.Text = "http://127.0.0.1:5000";
            if (string.IsNullOrWhiteSpace(tbProjectId.Text))
                tbProjectId.Text = "P001";

            BuildRootTree();
            BindTree();

            StartOrRestartPoller(force: true);

            lblStatus.Text = "Ready.";
        }

        private void BrowserWindow_Closed(object sender, EventArgs e)
        {
            _publishCts?.Cancel();
            _publishCts?.Dispose();
            StopPoller();
        }

        private void btnRefresh_Click(object sender, RoutedEventArgs e)
        {
            BuildRootTree();
            ApplySearch();
            lblStatus.Text = "Refreshed.";
        }

        private void tbSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplySearch();
        }

        private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var node = tvRoot?.SelectedItem as BrowserNode;
            if (node == null || !node.IsLeaf) return;
            if (node.ElementId == null || node.ElementId == ElementId.InvalidElementId) return;

            ActivateRequest.Set(node.ElementId);
            _activateEvent?.Raise();
        }

        // ==========================================================
        // QUY TRÌNH PUBLISH (INDEX -> EXPORT GLB SIÊU TỐC -> UPLOAD)
        // ==========================================================
        private async void btnPublish_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _publishCts?.Cancel();
                _publishCts = new CancellationTokenSource();

                btnPublish.IsEnabled = false;

                StartOrRestartPoller(force: false);
                var server = (tbServer.Text ?? "").Trim().TrimEnd('/');
                var projectId = (tbProjectId.Text ?? "").Trim();

                if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(projectId))
                {
                    MessageBox.Show("Server hoặc ProjectId không hợp lệ.", "Publish", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // --- BƯỚC 1: PUBLISH INDEX ---
                lblStatus.Text = "Step 1/3: Publishing Browser Index...";
                var index = BuildBrowserIndex(projectId);
                var urlIndex = $"{server}/api/projects/{Uri.EscapeDataString(projectId)}/browser-index";
                var jsonIndex = JsonConvert.SerializeObject(index);

                var reqIndex = new HttpRequestMessage(HttpMethod.Post, urlIndex);
                reqIndex.Headers.Add("X-API-Key", API_KEY);
                reqIndex.Content = new StringContent(jsonIndex, Encoding.UTF8, "application/json");

                var resIndex = await SharedHttpClient.SendAsync(reqIndex, _publishCts.Token);
                if (!resIndex.IsSuccessStatusCode)
                {
                    var err = await resIndex.Content.ReadAsStringAsync();
                    throw new Exception($"Lỗi gửi Index: {resIndex.StatusCode} - {err}");
                }

                // --- BƯỚC 2: EXPORT GLB SIÊU TỐC (Dùng OpenUI) ---
                lblStatus.Text = "Step 2/3: Exporting 3D Model (GLB) Fast mode...";

                string tempFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports", "TempUpload");
                Directory.CreateDirectory(tempFolder);
                string glbPath = Path.Combine(tempFolder, $"revit_project_{Guid.NewGuid():N}.glb");

                // Đặt View = null để lấy toàn bộ dự án
                ViewExportContext.SelectedViewId = null;

                // Kích hoạt External Event để bóc tách hình học
                OpenUI.CollectTcs = new TaskCompletionSource<bool>();
                OpenUI.CollectEvent?.Raise();

                // Đợi Revit chạy xong (Timeout 10 phút)
                var completed = await Task.WhenAny(OpenUI.CollectTcs.Task, Task.Delay(TimeSpan.FromMinutes(10)));
                if (completed != OpenUI.CollectTcs.Task)
                {
                    throw new Exception("Hết thời gian chờ bóc tách dữ liệu từ Revit.");
                }

                // Lấy kết quả Stream
                var stream = OpenUI.LastStream;
                if (stream == null || stream.objects == null || stream.objects.Count == 0)
                {
                    throw new Exception("Không có dữ liệu hình học để xuất.");
                }

                // Ghi ra file GLB bằng luồng phụ để không đơ UI
                await Task.Run(() =>
                {
                    GLBExporter.ExportToGLB(stream, glbPath);
                });

                if (!File.Exists(glbPath)) throw new Exception("Không tìm thấy file GLB sau khi export.");

                // --- BƯỚC 3: UPLOAD GLB LÊN SERVER ---
                lblStatus.Text = "Step 3/3: Uploading 3D Model...";
                await UploadGlbAsync(server, projectId, glbPath, _publishCts.Token);

                // Dọn dẹp file tạm
                try { File.Delete(glbPath); } catch { }

                lblStatus.Text = "All Done! Web Viewer is ready.";
                MessageBox.Show("Đồng bộ hoàn tất!\n\nMô hình 3D đã được tải lên.\nBây giờ bạn có thể nhấn 'Open 3D Viewer' trên web.", "Success");
            }
            catch (OperationCanceledException)
            {
                lblStatus.Text = "Publish cancelled.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi: {ex.Message}", "Publish Error", MessageBoxButton.OK, MessageBoxImage.Error);
                lblStatus.Text = "Publish error.";
            }
            finally
            {
                btnPublish.IsEnabled = true;
            }
        }

        private async Task UploadGlbAsync(string serverUrl, string projectId, string filePath, CancellationToken token)
        {
            var url = $"{serverUrl}/upload";

            using (var content = new MultipartFormDataContent())
            {
                content.Add(new StringContent(projectId), "projectId");

                using (var fileStream = File.OpenRead(filePath))
                {
                    var fileContent = new StreamContent(fileStream);
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    content.Add(fileContent, "file", Path.GetFileName(filePath));

                    var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Headers.Add("X-API-Key", API_KEY);
                    request.Content = content;

                    var response = await SharedHttpClient.SendAsync(request, token);
                    var responseBody = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new Exception($"Upload thất bại ({response.StatusCode}): {responseBody}");
                    }
                }
            }
        }

        // =================================================================
        // POLLER VÀ TREE BUILDER (GIỮ NGUYÊN)
        // =================================================================
        private void StartOrRestartPoller(bool force)
        {
            var server = (tbServer.Text ?? "").Trim().TrimEnd('/');
            var projectId = (tbProjectId.Text ?? "").Trim();
            var clientId = Environment.MachineName;

            RemoteSettings.ServerBaseUrl = server;
            RemoteSettings.ProjectId = projectId;

            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(projectId)) return;

            if (_remoteHandler == null)
            {
                _remoteHandler = new RemoteCommandHandler();
                _remoteEvent = ExternalEvent.Create(_remoteHandler);
            }

            if (!force && _poller != null &&
                string.Equals(server, _pollerServer, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(projectId, _pollerProjectId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            StopPoller();

            _pollerServer = server;
            _pollerProjectId = projectId;
            _pollerClientId = clientId;

            _poller = new RemoteCommandPoller(server, projectId, clientId, _remoteEvent);
        }

        private void StopPoller()
        {
            try { _poller?.Dispose(); } catch { }
            _poller = null;
        }

        private void BindTree() => tvRoot.ItemsSource = new[] { _root };

        private void ApplySearch()
        {
            var q = (tbSearch.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(q)) { BindTree(); return; }
            var filtered = FilterTree(_root, q);
            tvRoot.ItemsSource = filtered != null ? new[] { filtered } : Array.Empty<BrowserNode>();
        }

        private BrowserNode FilterTree(BrowserNode node, string q)
        {
            if (node == null) return null;
            if (node.IsLeaf)
            {
                if ((node.Title ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) return node;
                return null;
            }
            var copy = new BrowserNode { Title = node.Title, Type = node.Type, ElementId = node.ElementId };
            foreach (var c in node.Children)
            {
                var fc = FilterTree(c, q);
                if (fc != null) copy.Children.Add(fc);
            }
            return copy.Children.Count > 0 ? copy : null;
        }

        private void BuildRootTree()
        {
            _root = new BrowserNode { Title = "Project Browser", Type = BrowserNodeType.Folder };
            _root.Children.Add(BuildViewsTree(_doc));
            _root.Children.Add(BuildSheetsTree(_doc));
            _root.Children.Add(BuildSchedulesTree(_doc));
            _root.Children.Add(BuildFamiliesTree(_doc));
            _root.Children.Add(BuildGroupsTree(_doc));
            _root.Children.Add(BuildRevitLinksTree(_doc));
        }

        private BrowserNode BuildViewsTree(Document doc)
        {
            var org = BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc);
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).Cast<Element>().ToList();
            return BrowserTreeBuilder.BuildTree(doc, "Views", views, org, e => e.Name);
        }
        private BrowserNode BuildSheetsTree(Document doc)
        {
            var org = BrowserOrganization.GetCurrentBrowserOrganizationForSheets(doc);
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsTemplate).Cast<Element>().ToList();
            return BrowserTreeBuilder.BuildTree(doc, "Sheets", sheets, org, e => { var s = e as ViewSheet; return s == null ? e.Name : $"{s.SheetNumber} - {s.Name}"; });
        }
        private BrowserNode BuildSchedulesTree(Document doc)
        {
            var org = BrowserOrganization.GetCurrentBrowserOrganizationForSchedules(doc);
            var schedules = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Where(v => !v.IsTemplate).Cast<Element>().ToList();
            var root = BrowserTreeBuilder.BuildTree(doc, "Schedules/Quantities", schedules, org, e => e.Name);
            TryAppendPanelSchedules(doc, root);
            return root;
        }
        private void TryAppendPanelSchedules(Document doc, BrowserNode root)
        {
            try
            {
                var type = Type.GetType("Autodesk.Revit.DB.Electrical.PanelScheduleView, RevitAPI");
                if (type == null) return;
                var mi = typeof(BrowserOrganization).GetMethod("GetCurrentBrowserOrganizationForPanelSchedules", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var org = mi?.Invoke(null, new object[] { doc }) as BrowserOrganization;
                if (org == null) return;
                var panels = new FilteredElementCollector(doc).OfClass(type).Cast<Element>().ToList();
                if (panels.Any()) root.Children.Add(BrowserTreeBuilder.BuildTree(doc, "Panel Schedules", panels, org, e => e.Name));
            }
            catch { }
        }
        private BrowserNode BuildFamiliesTree(Document doc)
        {
            var root = new BrowserNode { Title = "Families", Type = BrowserNodeType.Folder };
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
            var byCategory = symbols.GroupBy(s => s.Category?.Name ?? "Unknown").OrderBy(g => g.Key);
            foreach (var catGroup in byCategory)
            {
                var catNode = new BrowserNode { Title = catGroup.Key, Type = BrowserNodeType.Folder };
                var byFamily = catGroup.GroupBy(s => s.Family?.Name ?? "(No Family)").OrderBy(g => g.Key);
                foreach (var famGroup in byFamily)
                {
                    var famNode = new BrowserNode { Title = famGroup.Key, Type = BrowserNodeType.Folder };
                    foreach (var sym in famGroup.OrderBy(x => x.Name))
                        famNode.Children.Add(new BrowserNode { Title = sym.Name, Type = BrowserNodeType.Item, ElementId = sym.Id });
                    if (famNode.Children.Count > 0) catNode.Children.Add(famNode);
                }
                if (catNode.Children.Count > 0) root.Children.Add(catNode);
            }
            return root;
        }
        private BrowserNode BuildGroupsTree(Document doc)
        {
            var root = new BrowserNode { Title = "Groups", Type = BrowserNodeType.Folder };
            var groupTypes = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Cast<GroupType>().ToList();
            var byCat = groupTypes.GroupBy(gt => gt.Category?.Name ?? "Unknown").OrderBy(g => g.Key);
            foreach (var catGroup in byCat)
            {
                var catNode = new BrowserNode { Title = catGroup.Key, Type = BrowserNodeType.Folder };
                foreach (var gt in catGroup.OrderBy(x => x.Name))
                    catNode.Children.Add(new BrowserNode { Title = gt.Name, Type = BrowserNodeType.Item, ElementId = gt.Id });
                if (catNode.Children.Count > 0) root.Children.Add(catNode);
            }
            return root;
        }
        private BrowserNode BuildRevitLinksTree(Document doc)
        {
            var root = new BrowserNode { Title = "Revit Links", Type = BrowserNodeType.Folder };
            var linkTypes = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().ToList();
            var linkInstances = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
            var instByType = linkInstances.GroupBy(i => i.GetTypeId()).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var lt in linkTypes.OrderBy(x => x.Name))
            {
                var typeNode = new BrowserNode { Title = lt.Name, Type = BrowserNodeType.Folder, ElementId = lt.Id };
                if (instByType.TryGetValue(lt.Id, out var insts))
                    foreach (var inst in insts.OrderBy(x => x.Name))
                        typeNode.Children.Add(new BrowserNode { Title = inst.Name, Type = BrowserNodeType.Item, ElementId = inst.Id });
                root.Children.Add(typeNode);
            }
            return root;
        }

        private object BuildBrowserIndex(string projectId)
        {
            var nodes = new List<object>();
            foreach (var branch in _root.Children) TraverseLeaf(branch, new List<string>(), nodes);

            string title = "";
            try { title = _doc?.Title ?? ""; } catch { }

            return new Dictionary<string, object>
            {
                ["projectId"] = projectId,
                ["generatedAt"] = DateTimeOffset.Now.ToString("o"),
                ["revitModelTitle"] = title,
                ["nodes"] = nodes
            };
        }

        private void TraverseLeaf(BrowserNode node, List<string> path, List<object> output)
        {
            if (node == null) return;
            if (node.Type == BrowserNodeType.Folder)
            {
                path.Add(node.Title);
                foreach (var c in node.Children) TraverseLeaf(c, path, output);
                path.RemoveAt(path.Count - 1);
                return;
            }

            var id = node.ElementId;
            if (id == ElementId.InvalidElementId) return;
            var elem = _doc.GetElement(id);
            if (elem == null) return;

            string kind = "item";
            if (elem is View v) kind = v is ViewSheet ? "sheet" : (v is ViewSchedule ? "schedule" : "view");
            else if (elem is FamilySymbol) kind = "family_type";
            else if (elem is GroupType) kind = "group_type";
            else if (elem is RevitLinkInstance) kind = "revit_link_instance";
            else if (elem is RevitLinkType) kind = "revit_link_type";

            var meta = new Dictionary<string, object>();
            if (elem is View vv) { try { meta["viewType"] = vv.ViewType.ToString(); } catch { } }

            output.Add(new Dictionary<string, object>
            {
                ["title"] = node.Title,
                ["kind"] = kind,
                ["path"] = path.ToArray(),
                ["revit"] = new Dictionary<string, object> { ["uniqueId"] = elem.UniqueId },
                ["meta"] = meta
            });
        }
    }
}