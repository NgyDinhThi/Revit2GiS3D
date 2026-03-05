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
            if (string.IsNullOrWhiteSpace(tbProjectId.Text)) tbProjectId.Text = "P001";
            if (string.IsNullOrWhiteSpace(tbUserName.Text)) tbUserName.Text = Environment.UserName;

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
                    lblStatus.Text = "Sẵn sàng hoạt động. Vui lòng bấm Tải lệnh về hoặc Đồng bộ!";
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
                    lblStatus.Text = $"Đã chuyển sang: {_doc.Title}. Vui lòng ĐỒNG BỘ hoặc TẢI LỆNH!";
                    lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(209, 52, 56));
                    StopPoller();
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
                else panelShare.Visibility = System.Windows.Visibility.Collapsed;
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
                // Gõ cửa API nội bộ của Ngrok (cổng 4040) để hỏi link Public
                var response = await SharedHttpClient.GetStringAsync("http://127.0.0.1:4040/api/tunnels");
                var json = JObject.Parse(response);
                var tunnels = json["tunnels"] as JArray;

                if (tunnels != null && tunnels.Count > 0)
                {
                    // Ưu tiên tìm link bảo mật (https)
                    foreach (var tunnel in tunnels)
                    {
                        string publicUrl = tunnel["public_url"]?.ToString();
                        if (!string.IsNullOrEmpty(publicUrl) && publicUrl.StartsWith("https"))
                        {
                            return publicUrl;
                        }
                    }

                    // Nếu không có https thì lấy tạm link đầu tiên
                    string firstUrl = tunnels[0]["public_url"]?.ToString();
                    if (!string.IsNullOrEmpty(firstUrl)) return firstUrl;
                }
            }
            catch
            {

            }
            return "http://127.0.0.1:5000";
        }

        // ĐÂY LÀ HÀM btnPull_Click ĐÃ ĐƯỢC THÊM LẠI
        private void btnPull_Click(object sender, RoutedEventArgs e)
        {
            string inputProjectId = tbProjectId.Text.Trim();
            if (string.IsNullOrWhiteSpace(inputProjectId))
            {
                MessageBox.Show("Vui lòng nhập Mã Dự Án!", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (cbDocuments.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            {
                _doc = item.Tag as Document;
                if (_doc != null) RemoteSettings.TargetDocumentTitle = _doc.Title;
            }

            if (_doc == null) return;

            lblStatus.Text = $"Đang kéo dữ liệu dự án [{inputProjectId}] về file [{_doc.Title}]...";
            lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212));

            RemoteSettings.ProjectId = inputProjectId;
            RemoteCommandPoller.ClearMemory();
            StartOrRestartPoller(force: true);

            Task.Run(async () =>
            {
                await Task.Delay(2000);
                Dispatcher.Invoke(() =>
                {
                    lblStatus.Text = "Đã quét xong! Các thay đổi (nếu có) đã được cập nhật.";
                    lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69));
                });
            });
        }

        private async void btnPublish_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string inputProjectId = tbProjectId.Text.Trim();
                string userName = tbUserName.Text.Trim();
                if (string.IsNullOrWhiteSpace(userName)) userName = "Người dùng Revit";
                bool includeRvt = chkIncludeRvt.IsChecked == true;

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

                lblStatus.Text = "Bước 1/4: Đang thu thập dữ liệu cây thư mục...";
                BuildRootTree();
                var index = BuildBrowserIndex(inputProjectId);

                var urlIndex = $"{server}/api/projects/{Uri.EscapeDataString(inputProjectId)}/browser-index";
                var reqIndex = new HttpRequestMessage(HttpMethod.Post, urlIndex);
                reqIndex.Headers.Add("X-API-Key", API_KEY);
                reqIndex.Headers.Add("X-User-Name", Uri.EscapeDataString(userName));
                reqIndex.Content = new StringContent(JsonConvert.SerializeObject(index), Encoding.UTF8, "application/json");

                var resIndex = await SharedHttpClient.SendAsync(reqIndex, _publishCts.Token);
                if (!resIndex.IsSuccessStatusCode) throw new Exception("Lỗi gửi Index.");

                lblStatus.Text = "Bước 2/4: Đang trích xuất mô hình 3D (GLB)...";
                string tempFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports", "TempUpload");
                Directory.CreateDirectory(tempFolder);
                string glbPath = Path.Combine(tempFolder, $"revit_project_{Guid.NewGuid():N}.glb");

                OpenUI.CollectTcs = new TaskCompletionSource<bool>();
                OpenUI.CollectEvent?.Raise();
                await Task.WhenAny(OpenUI.CollectTcs.Task, Task.Delay(TimeSpan.FromMinutes(10)));
                var stream = OpenUI.LastStream;

                await Task.Run(() => GLBExporter.ExportToGLB(stream, glbPath));

                lblStatus.Text = "Bước 3/4: Đang tải dữ liệu GLB lên Server...";
                await UploadFileAsync(server, inputProjectId, glbPath, userName, _publishCts.Token);
                try { File.Delete(glbPath); } catch { }

                if (includeRvt)
                {
                    lblStatus.Text = "Bước 4/4: Đang tải file Revit (.rvt) lên Server (vài phút)...";
                    string rvtPath = _doc.PathName;

                    if (string.IsNullOrWhiteSpace(rvtPath))
                        throw new Exception("File chưa được lưu vào máy tính. Vui lòng bấm 'Save' file Revit trước khi đính kèm!");

                    string tempRvt = Path.Combine(tempFolder, Path.GetFileName(rvtPath));

                    File.Copy(rvtPath, tempRvt, true);

                    await UploadFileAsync(server, inputProjectId, tempRvt, userName, _publishCts.Token);
                    try { File.Delete(tempRvt); } catch { }
                }

                _isPublished = true;
                UpdateShareLink();

                lblStatus.Text = "Đồng bộ thành công! Bạn có thể Copy link ngay bây giờ.";
                lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                lblStatus.Text = "Lỗi trong quá trình đồng bộ.";
                lblStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(209, 52, 56));
            }
            finally
            {
                btnPublish.IsEnabled = true;
                btnPull.IsEnabled = true;
            }
        }

        private async Task UploadFileAsync(string serverUrl, string projectId, string filePath, string userName, CancellationToken token)
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
                    request.Headers.Add("X-User-Name", Uri.EscapeDataString(userName));
                    request.Content = content;

                    var response = await SharedHttpClient.SendAsync(request, token);
                    if (!response.IsSuccessStatusCode) throw new Exception("Upload thất bại.");
                }
            }
        }

        private void StartOrRestartPoller(bool force)
        {
            var server = INTERNAL_SERVER_URL;
            var projectId = (tbProjectId.Text ?? "").Trim();
            var userName = (tbUserName.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(userName)) userName = "Người dùng ẩn danh";

            RemoteSettings.ServerBaseUrl = server;
            RemoteSettings.ProjectId = projectId;

            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(projectId)) return;

            if (!force && _poller != null && _pollerServer == server && _pollerProjectId == projectId) return;

            StopPoller();
            _pollerServer = server;
            _pollerProjectId = projectId;

            _poller = new RemoteCommandPoller(server, projectId, "default", userName, _remoteEvent);
        }

        private void StopPoller() { try { _poller?.Dispose(); } catch { } _poller = null; }

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

        private BrowserNode BuildViewsTree(Document doc) { var org = BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc); var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).Cast<Element>().ToList(); return BrowserTreeBuilder.BuildTree(doc, "Views", views, org, e => e.Name); }
        private BrowserNode BuildSheetsTree(Document doc) { var org = BrowserOrganization.GetCurrentBrowserOrganizationForSheets(doc); var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsTemplate).Cast<Element>().ToList(); return BrowserTreeBuilder.BuildTree(doc, "Sheets", sheets, org, e => { var s = e as ViewSheet; return s == null ? e.Name : $"{s.SheetNumber} - {s.Name}"; }); }
        private BrowserNode BuildSchedulesTree(Document doc) { var org = BrowserOrganization.GetCurrentBrowserOrganizationForSchedules(doc); var schedules = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Where(v => !v.IsTemplate).Cast<Element>().ToList(); return BrowserTreeBuilder.BuildTree(doc, "Schedules/Quantities", schedules, org, e => e.Name); }
        private BrowserNode BuildFamiliesTree(Document doc) { var root = new BrowserNode { Title = "Families", Type = BrowserNodeType.Folder }; return root; }
        private BrowserNode BuildGroupsTree(Document doc) { var root = new BrowserNode { Title = "Groups", Type = BrowserNodeType.Folder }; return root; }
        private BrowserNode BuildRevitLinksTree(Document doc) { var root = new BrowserNode { Title = "Revit Links", Type = BrowserNodeType.Folder }; return root; }

        private object BuildBrowserIndex(string projectId)
        {
            var nodes = new List<object>();
            foreach (var branch in _root.Children) TraverseLeaf(branch, new List<string>(), nodes);
            return new Dictionary<string, object> { ["projectId"] = projectId, ["nodes"] = nodes };
        }

        private void TraverseLeaf(BrowserNode node, List<string> path, List<object> output)
        {
            if (node == null) return;
            if (node.Type == BrowserNodeType.Folder) { path.Add(node.Title); foreach (var c in node.Children) TraverseLeaf(c, path, output); path.RemoveAt(path.Count - 1); return; }
            var id = node.ElementId; if (id == ElementId.InvalidElementId) return; var elem = _doc.GetElement(id); if (elem == null) return;

            string kind = "item";
            if (elem is View v) kind = v is ViewSheet ? "sheet" : (v is ViewSchedule ? "schedule" : "view");
            output.Add(new Dictionary<string, object> { ["title"] = node.Title, ["kind"] = kind, ["path"] = path.ToArray(), ["revit"] = new Dictionary<string, object> { ["uniqueId"] = elem.UniqueId } });
        }
    }
}