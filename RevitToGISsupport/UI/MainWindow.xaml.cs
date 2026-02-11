using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitToGISsupport.Models;
using RevitToGISsupport.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;

namespace RevitToGISsupport.UI
{
    public partial class MainWindows : Window
    {
        private Document _doc;

        public MainWindows()
        {
            InitializeComponent();
            Loaded += MainWindows_Loaded;
        }

        private void MainWindows_Loaded(object sender, RoutedEventArgs e)
        {
            _doc = OpenUI.ActiveDoc ?? OpenUI.UiApp?.ActiveUIDocument?.Document;

            if (_doc == null)
            {
                lblStatus.Text = "Không lấy được Document. Hãy mở model và chạy lại lệnh add-in.";
                return;
            }

            LoadViews();
            lblStatus.Text = "Sẵn sàng.";
        }

        private void LoadViews()
        {
            if (_doc == null) return;

            var views = ViewExportContext.GetExportableViews(_doc);
            cbView.ItemsSource = views;

            if (_doc.ActiveView != null)
                cbView.SelectedItem = _doc.ActiveView;
        }

        private void SetSelectedViewToContext()
        {
            var selectedView = cbView.SelectedItem as View;
            ViewExportContext.SelectedViewId = selectedView?.Id;
        }

        private async void btnCollect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                btnCollect.IsEnabled = false;
                lblStatus.Text = "Đang thu thập dữ liệu từ Revit...";

                _doc = OpenUI.ActiveDoc ?? OpenUI.UiApp?.ActiveUIDocument?.Document;
                if (_doc == null)
                {
                    MessageBox.Show("Không lấy được Document. Hãy mở model trong Revit.", "Collect",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (cbView.ItemsSource == null)
                    LoadViews();

                SetSelectedViewToContext();

                OpenUI.CollectTcs = new TaskCompletionSource<bool>();
                if (OpenUI.CollectEvent == null)
                {
                    MessageBox.Show("ExternalEvent chưa được khởi tạo. Hãy chạy lệnh add-in mở UI từ Revit.", "Collect",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                OpenUI.CollectEvent.Raise();

                var completed = await Task.WhenAny(OpenUI.CollectTcs.Task, Task.Delay(TimeSpan.FromMinutes(10)));
                if (completed != OpenUI.CollectTcs.Task)
                {
                    MessageBox.Show("Hết thời gian chờ (10 phút).", "Timeout",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    lblStatus.Text = "Timeout";
                    return;
                }

                var stream = OpenUI.LastStream;
                if (stream == null || stream.objects == null || stream.objects.Count == 0)
                {
                    MessageBox.Show("Không thu được dữ liệu (stream rỗng).", "Collect",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    lblStatus.Text = "Không có dữ liệu";
                    return;
                }

                lblStatus.Text = $"Đã thu thập {stream.objects.Count} đối tượng";

                try
                {
                    var exportFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports");
                    Directory.CreateDirectory(exportFolder);
                    var jsonPath = Path.Combine(exportFolder, "revit_model.json");
                    File.WriteAllText(jsonPath, JsonConvert.SerializeObject(stream.ToGeoJson(), Formatting.Indented));
                }
                catch { }

                MessageBox.Show($"Thu thập xong: {stream.objects.Count} đối tượng.", "Collect",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi khi thu thập: {ex.Message}", "Collect",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                lblStatus.Text = "Lỗi thu thập";
            }
            finally
            {
                btnCollect.IsEnabled = true;
            }
        }

        private async void btnExportGLB_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var stream = OpenUI.LastStream;
                if (stream == null)
                {
                    MessageBox.Show("Chưa có dữ liệu. Hãy Collect trước.", "Export",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string folderPath = null;
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dlg.Description = "Chọn thư mục để lưu revit_model.json và revit_model.glb";
                    dlg.ShowNewFolderButton = true;
                    dlg.SelectedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports");
                    var res = dlg.ShowDialog();
                    if (res == System.Windows.Forms.DialogResult.OK || res == System.Windows.Forms.DialogResult.Yes)
                        folderPath = dlg.SelectedPath;
                }
                if (string.IsNullOrWhiteSpace(folderPath)) return;

                btnExportGLB.IsEnabled = false;
                lblStatus.Text = "Đang export...";

                await Task.Run(() => ExportService.ExportJsonAndGlb(stream, folderPath));

                lblStatus.Text = "Xuất xong";
                MessageBox.Show(
                    $"Đã xuất xong:\n- JSON: {Path.Combine(folderPath, "revit_model.json")}\n- GLB: {Path.Combine(folderPath, "revit_model.glb")}",
                    "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi khi export: " + ex.Message, "Export",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                lblStatus.Text = "Lỗi export";
            }
            finally
            {
                btnExportGLB.IsEnabled = true;
            }
        }

        private async void btnOnlineWatch_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetSelectedViewToContext();

                var stream = OpenUI.LastStream;
                if (stream == null)
                {
                    lblStatus.Text = "Đang tự động thu thập...";
                    btnOnlineWatch.IsEnabled = false;

                    OpenUI.CollectTcs = new TaskCompletionSource<bool>();
                    OpenUI.CollectEvent?.Raise();
                    await OpenUI.CollectTcs.Task;

                    stream = OpenUI.LastStream;
                }

                if (stream == null || stream.objects == null || stream.objects.Count == 0)
                {
                    MessageBox.Show("Không có dữ liệu để upload.", "Online Watch",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    lblStatus.Text = "Không có dữ liệu";
                    return;
                }

                btnOnlineWatch.IsEnabled = false;
                lblStatus.Text = "Đang export...";

                string exportFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports");
                Directory.CreateDirectory(exportFolder);

                await Task.Run(() => ExportService.ExportJsonAndGlb(stream, exportFolder));

                lblStatus.Text = "Đang gửi dữ liệu lên server...";
                string viewerUrl = await UploadJsonAndGlbAsync("http://127.0.0.1:5000", exportFolder);

                lblStatus.Text = "Đang mở trình duyệt...";
                Process.Start(new ProcessStartInfo(viewerUrl) { UseShellExecute = true });

                lblStatus.Text = "Hoàn tất.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi: {ex.Message}", "Online View",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                lblStatus.Text = "Gặp lỗi.";
            }
            finally
            {
                btnOnlineWatch.IsEnabled = true;
            }
        }

        private static async Task<string> UploadJsonAndGlbAsync(string flaskBaseUrl, string folderPath)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var form = new MultipartFormDataContent();

            var jsonPath = Path.Combine(folderPath, "revit_model.json");
            var glbPath = Path.Combine(folderPath, "revit_model.glb");

            if (File.Exists(jsonPath))
                form.Add(new StreamContent(File.OpenRead(jsonPath)), "file", Path.GetFileName(jsonPath));

            if (File.Exists(glbPath))
                form.Add(new StreamContent(File.OpenRead(glbPath)), "file", Path.GetFileName(glbPath));

            var url = $"{flaskBaseUrl.TrimEnd('/')}/upload";
            var res = await http.PostAsync(url, form);
            var body = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
                throw new Exception($"Flask upload failed ({(int)res.StatusCode}): {body}");

            dynamic obj = JsonConvert.DeserializeObject(body);
            string viewerUrl = (string)(obj?.viewer_url ?? obj?.url);

            if (string.IsNullOrWhiteSpace(viewerUrl))
                throw new Exception("Flask không trả về 'viewer_url'.");

            if (viewerUrl.StartsWith("/"))
                viewerUrl = flaskBaseUrl.TrimEnd('/') + viewerUrl;

            return viewerUrl;
        }
    }
}