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

namespace RevitToGISsupport.UI
{
    public partial class BrowserWindow : Window
    {
        private readonly UIApplication _uiapp;
        private readonly Document _doc;
        private readonly ExternalEvent _activateEvent;

        // Lưu trữ cây dữ liệu ngầm (Không hiển thị ra giao diện nữa)
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
                MessageBox.Show("Không lấy được Document.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
                return;
            }

            if (string.IsNullOrWhiteSpace(tbServer.Text))
                tbServer.Text = "http://127.0.0.1:5000";
            if (string.IsNullOrWhiteSpace(tbProjectId.Text))
                tbProjectId.Text = "P001";

            // Khởi động lắng nghe Web -> Revit
            StartOrRestartPoller(force: true);

            lblStatus.Text = "Sẵn sàng hoạt động.";
            lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69)); // Xanh lá
        }

        private void BrowserWindow_Closed(object sender, EventArgs e)
        {
            _publishCts?.Cancel();
            _publishCts?.Dispose();
            StopPoller();
        }

        // ==========================================================
        // QUY TRÌNH PUBLISH (TẠO INDEX NGẦM -> GLB -> UPLOAD)
        // ==========================================================
        private async void btnPublish_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _publishCts?.Cancel();
                _publishCts = new CancellationTokenSource();

                btnPublish.IsEnabled = false;
                lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212)); // Xanh dương

                StartOrRestartPoller(force: false);
                var server = (tbServer.Text ?? "").Trim().TrimEnd('/');
                var projectId = (tbProjectId.Text ?? "").Trim();

                if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(projectId))
                {
                    MessageBox.Show("Server hoặc ProjectId không hợp lệ.", "Publish", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // --- BƯỚC 1: TỰ ĐỘNG THU THẬP & PUBLISH INDEX NGẦM ---
                lblStatus.Text = "Bước 1/3: Đang thu thập dữ liệu cây thư mục...";

                // Thu thập cấu trúc mới nhất ngầm bên trong bộ nhớ
                BuildRootTree();
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
                lblStatus.Text = "Bước 2/3: Đang trích xuất mô hình 3D (GLB)...";

                string tempFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports", "TempUpload");
                Directory.CreateDirectory(tempFolder);
                string glbPath = Path.Combine(tempFolder, $"revit_project_{Guid.NewGuid():N}.glb");

                ViewExportContext.SelectedViewId = null;

                OpenUI.CollectTcs = new TaskCompletionSource<bool>();
                OpenUI.CollectEvent?.Raise();

                var completed = await Task.WhenAny(OpenUI.CollectTcs.Task, Task.Delay(TimeSpan.FromMinutes(10)));
                if (completed != OpenUI.CollectTcs.Task)
                {
                    throw new Exception("Hết thời gian chờ bóc tách dữ liệu từ Revit.");
                }

                var stream = OpenUI.LastStream;
                if (stream == null || stream.objects == null || stream.objects.Count == 0)
                {
                    throw new Exception("Không có dữ liệu hình học để xuất.");
                }

                await Task.Run(() =>
                {
                    GLBExporter.ExportToGLB(stream, glbPath);
                });

                if (!File.Exists(glbPath)) throw new Exception("Không tìm thấy file GLB sau khi export.");

                // --- BƯỚC 3: UPLOAD GLB LÊN SERVER ---
                lblStatus.Text = "Bước 3/3: Đang tải dữ liệu lên Server...";
                await UploadGlbAsync(server, projectId, glbPath, _publishCts.Token);

                try { File.Delete(glbPath); } catch { }

                lblStatus.Text = "Đồng bộ thành công! Có thể xem trên Web.";
                lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69)); // Xanh lá
                MessageBox.Show("Đồng bộ hoàn tất!\n\nMô hình 3D và Dữ liệu đã được tải lên Server.", "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                lblStatus.Text = "Đã hủy tiến trình.";
                lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(209, 52, 56)); // Đỏ
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi: {ex.Message}", "Lỗi đồng bộ", MessageBoxButton.OK, MessageBoxImage.Error);
                lblStatus.Text = "Lỗi trong quá trình đồng bộ.";
                lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(209, 52, 56)); // Đỏ
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
        // HỆ THỐNG POLLER (Lắng nghe web) VÀ BUILDER (Chạy ngầm)
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

        // CÁC HÀM XÂY DỰNG CẤU TRÚC NGẦM ĐỂ XUẤT RA JSON
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