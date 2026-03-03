using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        private Document _doc;
        private readonly ExternalEvent _activateEvent;

        private BrowserNode _root;
        private const string API_KEY = "CHANGE-ME-IN-PRODUCTION";

        private static readonly HttpClient SharedHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        private CancellationTokenSource _publishCts;

        private static RemoteCommandHandler _remoteHandler;
        private static ExternalEvent _remoteEvent;
        private RemoteCommandPoller _poller;

        private string _pollerServer;
        private string _pollerProjectId;
        private string _pollerClientId;

        private const string INTERNAL_SERVER_URL = "http://127.0.0.1:5000";
        private string _baseShareUrl = "http://127.0.0.1:5000";
        private bool _isPublished = false;

        public BrowserWindow(UIApplication uiapp, ExternalEvent activateEvent)
        {
            InitializeComponent();

            _uiapp = uiapp;
            _doc = uiapp?.ActiveUIDocument?.Document;
            _activateEvent = activateEvent;

            if (_remoteHandler == null)
            {
                _remoteHandler = new RemoteCommandHandler();
                _remoteEvent = ExternalEvent.Create(_remoteHandler);
            }

            Loaded += BrowserWindow_Loaded;
            Closed += BrowserWindow_Closed;
        }

        private void BrowserWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(tbProjectId.Text))
                tbProjectId.Text = "P001";

            cbDocuments.Items.Clear();
            foreach (Document d in _uiapp.Application.Documents)
            {
                if (d.IsLinked) continue;

                var item = new System.Windows.Controls.ComboBoxItem { Content = d.Title, Tag = d };
                cbDocuments.Items.Add(item);

                if (_uiapp.ActiveUIDocument != null && d.Title == _uiapp.ActiveUIDocument.Document.Title)
                {
                    cbDocuments.SelectedItem = item;
                    _doc = d;
                }
            }

            if (cbDocuments.SelectedIndex == -1 && cbDocuments.Items.Count > 0)
            {
                cbDocuments.SelectedIndex = 0;
                _doc = (cbDocuments.Items[0] as System.Windows.Controls.ComboBoxItem).Tag as Document;
            }

            if (_doc == null)
            {
                MessageBox.Show("Không tìm thấy Document nào đang mở.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
                return;
            }

            RemoteSettings.TargetDocumentTitle = _doc.Title;

            lblStatus.Text = "Đang tìm địa chỉ kết nối...";
            lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212));

            Task.Run(async () =>
            {
                string url = await DetectPublicUrlAsyncSafe();

                Dispatcher.Invoke(() =>
                {
                    _baseShareUrl = url;
                    UpdateShareLink();
                    lblStatus.Text = "Sẵn sàng hoạt động. Vui lòng Đồng bộ lên Web!";
                    lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69));
                });
            });
        }

        private void BrowserWindow_Closed(object sender, EventArgs e)
        {
            _publishCts?.Cancel();
            _publishCts?.Dispose();
            StopPoller();
        }

        private void cbDocuments_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (cbDocuments.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            {
                _doc = item.Tag as Document;
                if (_doc != null)
                {
                    RemoteSettings.TargetDocumentTitle = _doc.Title;

                    _isPublished = false;
                    UpdateShareLink();
                    lblStatus.Text = $"Đã chuyển sang: {_doc.Title}. Vui lòng ĐỒNG BỘ LÊN WEB!";
                    lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(209, 52, 56));

                    StartOrRestartPoller(force: true);
                }
            }
        }

        private void tbProjectId_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateShareLink();
        }

        private void UpdateShareLink()
        {
            if (tbShareLink != null && panelShare != null)
            {
                if (_isPublished)
                {
                    panelShare.Visibility = System.Windows.Visibility.Visible;
                    var pid = tbProjectId?.Text?.Trim() ?? "P001";
                    tbShareLink.Text = $"{_baseShareUrl}/browser?projectId={pid}";
                }
                else
                {
                    panelShare.Visibility = System.Windows.Visibility.Collapsed;
                }
            }
        }

        private async void btnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_isPublished && !string.IsNullOrWhiteSpace(tbShareLink.Text))
            {
                Clipboard.SetText(tbShareLink.Text);

                string oldText = btnCopy.Content.ToString();
                btnCopy.Content = "Đã Copy!";
                btnCopy.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(223, 246, 221));
                btnCopy.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69));

                lblStatus.Text = "Đã copy link vào khay nhớ tạm.";
                lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69));

                await Task.Delay(2000);

                btnCopy.Content = oldText;
                btnCopy.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255));
                btnCopy.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 49, 48));
            }
        }

        private async Task<string> DetectPublicUrlAsyncSafe()
        {
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
                {
                    var res = await client.GetAsync("http://127.0.0.1:4040/api/tunnels");
                    if (res.IsSuccessStatusCode)
                    {
                        var json = await res.Content.ReadAsStringAsync();
                        var data = JObject.Parse(json);
                        var tunnels = data["tunnels"];
                        if (tunnels != null)
                        {
                            foreach (var t in tunnels)
                            {
                                if (t["proto"]?.ToString() == "https")
                                    return t["public_url"]?.ToString();
                            }
                        }
                    }
                }
            }
            catch { }

            try
            {
                string localIP = "127.0.0.1";
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        localIP = ip.ToString();
                        break;
                    }
                }
                return $"http://{localIP}:5000";
            }
            catch { }

            return "http://127.0.0.1:5000";
        }

        // =================================================================
        // NÚT TẢI LỆNH TỪ WEB VỀ REVIT
        // =================================================================
        private void btnPull_Click(object sender, RoutedEventArgs e)
        {
            string inputProjectId = tbProjectId.Text.Trim();
            if (string.IsNullOrWhiteSpace(inputProjectId))
            {
                MessageBox.Show("Vui lòng nhập Mã Dự Án (Project ID) để tải lệnh!", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (cbDocuments.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            {
                _doc = item.Tag as Document;
                if (_doc != null)
                {
                    RemoteSettings.TargetDocumentTitle = _doc.Title;
                }
            }

            if (_doc == null)
            {
                MessageBox.Show("Vui lòng chọn một file Revit đang mở để nhận dữ liệu!", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            lblStatus.Text = $"Đang kéo dữ liệu của dự án [{inputProjectId}] về file [{_doc.Title}]...";
            lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212));

            // Ép hệ thống đồng bộ lại với Web để kéo nhật ký về
            RemoteSettings.ProjectId = inputProjectId;

            // Bắt Plugin quên hết các lệnh cũ để ép làm lại toàn bộ
            RemoteCommandPoller.ClearMemory();

            StartOrRestartPoller(force: true);

            Task.Run(async () =>
            {
                await Task.Delay(2000);
                Dispatcher.Invoke(() =>
                {
                    lblStatus.Text = "Đã quét xong! Các thay đổi (nếu có) đã được cập nhật vào file.";
                    lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69));
                });
            });
        }

        private async void btnPublish_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string inputProjectId = tbProjectId.Text.Trim();
                if (string.IsNullOrWhiteSpace(inputProjectId))
                {
                    MessageBox.Show("Vui lòng nhập Mã Dự Án!", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                RemoteSettings.ProjectId = inputProjectId;

                _publishCts?.Cancel();
                _publishCts = new CancellationTokenSource();

                _isPublished = false;
                UpdateShareLink();

                btnPublish.IsEnabled = false;
                btnPull.IsEnabled = false;
                lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212));

                StartOrRestartPoller(force: false);

                var server = INTERNAL_SERVER_URL;
                var projectId = RemoteSettings.ProjectId;

                lblStatus.Text = "Bước 1/3: Đang thu thập dữ liệu cây thư mục...";
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

                lblStatus.Text = "Bước 3/3: Đang tải dữ liệu lên Server...";
                await UploadGlbAsync(server, projectId, glbPath, _publishCts.Token);

                try { File.Delete(glbPath); } catch { }

                _isPublished = true;
                UpdateShareLink();

                lblStatus.Text = "Đồng bộ thành công! Bạn có thể Copy link ngay bây giờ.";
                lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69));
            }
            catch (OperationCanceledException)
            {
                lblStatus.Text = "Đã hủy tiến trình.";
                lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(209, 52, 56));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi: {ex.Message}", "Lỗi đồng bộ", MessageBoxButton.OK, MessageBoxImage.Error);
                lblStatus.Text = "Lỗi trong quá trình đồng bộ.";
                lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(209, 52, 56));
            }
            finally
            {
                btnPublish.IsEnabled = true;
                btnPull.IsEnabled = true;
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

        private void StartOrRestartPoller(bool force)
        {
            var server = INTERNAL_SERVER_URL;
            var projectId = (tbProjectId.Text ?? "").Trim();
            var clientId = Environment.MachineName;

            RemoteSettings.ServerBaseUrl = server;
            RemoteSettings.ProjectId = projectId;

            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(projectId)) return;

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