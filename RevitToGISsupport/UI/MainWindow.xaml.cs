using Newtonsoft.Json;
using RevitToGISsupport.Models;
using RevitToGISsupport.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace RevitToGISsupport.UI
{
    public partial class MainWindows : Window
    {
        public MainWindows()
        {
            InitializeComponent();
        }

        // ==========================
        //  COLLECT
        // ==========================
        private async void btnCollect_Click(object sender, RoutedEventArgs e)
        {
            // BẮT ĐẦU ĐO THỜI GIAN
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                btnCollect.IsEnabled = false;
                lblStatus.Text = "Đang thu thập dữ liệu từ Revit...";
                progressBar.Visibility = Visibility.Visible;
                progressBar.Value = 0;
                progressBar.Maximum = 100;

                OpenUI.ProgressReporter = new Progress<int>(pct =>
                {
                    if (pct < 0) pct = 0;
                    if (pct > 100) pct = 100;
                    progressBar.Value = pct;
                    lblStatus.Text = $"Đang thu thập... {pct}%";
                });

                OpenUI.CollectTcs = new TaskCompletionSource<bool>();
                if (OpenUI.CollectEvent == null)
                {
                    MessageBox.Show("ExternalEvent chưa được khởi tạo. Hãy mở UI từ lệnh Revit trước.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                OpenUI.CollectEvent.Raise();

                var completed = await Task.WhenAny(OpenUI.CollectTcs.Task, Task.Delay(TimeSpan.FromMinutes(10)));
                if (completed != OpenUI.CollectTcs.Task)
                {
                    MessageBox.Show("⏱️ Hết thời gian chờ (10 phút).", "Timeout", MessageBoxButton.OK, MessageBoxImage.Warning);
                    lblStatus.Text = "Hết thời gian chờ";
                    return;
                }

                stopwatch.Stop();
                var elapsed = stopwatch.Elapsed;

                var stream = OpenUI.LastStream;
                if (stream == null || stream.objects == null || stream.objects.Count == 0)
                {
                    MessageBox.Show("⚠️ Không thu được dữ liệu (stream rỗng).", "Collect", MessageBoxButton.OK, MessageBoxImage.Warning);
                    lblStatus.Text = "Không có dữ liệu";
                }
                else
                {
                    lblStatus.Text = $"Đã thu thập {stream.objects.Count} đối tượng";
                    try
                    {
                        var exportFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports");
                        Directory.CreateDirectory(exportFolder);
                        var jsonPath = Path.Combine(exportFolder, "revit_model.json");
                        File.WriteAllText(jsonPath, JsonConvert.SerializeObject(stream.ToGeoJson(), Formatting.Indented));
                    }
                    catch { /* ignore IO */ }

                    string timeString = $"{elapsed.Minutes} phút {elapsed.Seconds} giây";
                    MessageBox.Show($"✅ Thu thập xong: {stream.objects.Count} đối tượng.\n⏱️ Thời gian xử lý: {timeString}", "Collect", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi khi thu thập: " + ex.Message, "Collect", MessageBoxButton.OK, MessageBoxImage.Error);
                lblStatus.Text = "Lỗi thu thập";
            }
            finally
            {
                if (stopwatch.IsRunning) stopwatch.Stop();
                progressBar.Visibility = Visibility.Collapsed;
                progressBar.Value = 0;
                OpenUI.ProgressReporter = null;
                btnCollect.IsEnabled = true;
            }
        }

        // ==========================
        //  EXPORT JSON/GLB
        // ==========================
        private async void btnExportGLB_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                GISStream stream = OpenUI.LastStream;

                // *** THAY ĐỔI QUAN TRỌNG BẮT ĐẦU TỪ ĐÂY ***
                // Nếu chưa có dữ liệu, hãy tự động chạy Collect
                if (stream == null)
                {
                    MessageBox.Show("Chưa có dữ liệu. Chương trình sẽ tự động thu thập từ Revit.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);

                    lblStatus.Text = "Đang tự động thu thập...";
                    btnExportGLB.IsEnabled = false; // Vô hiệu hóa nút trong khi thu thập
                    progressBar.Visibility = Visibility.Visible;
                    progressBar.IsIndeterminate = true; // Thanh tiến trình chạy vô hạn

                    // Thực hiện quá trình Collect
                    OpenUI.CollectTcs = new TaskCompletionSource<bool>();
                    if (OpenUI.CollectEvent == null)
                    {
                        MessageBox.Show("ExternalEvent chưa được khởi tạo.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    OpenUI.CollectEvent.Raise();
                    await OpenUI.CollectTcs.Task; // Chờ cho đến khi Collect hoàn tất

                    // Lấy dữ liệu mới
                    stream = OpenUI.LastStream;

                    progressBar.Visibility = Visibility.Collapsed;
                    progressBar.IsIndeterminate = false;
                }
                // *** KẾT THÚC THAY ĐỔI ***

                if (stream == null)
                {
                    MessageBox.Show("❌ Thu thập dữ liệu thất bại, không thể export.", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Phần còn lại của hàm giữ nguyên: chọn thư mục và xuất file
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
                    $"✅ Đã xuất xong:\n- JSON: {Path.Combine(folderPath, "revit_model.json")}\n- GLB: {Path.Combine(folderPath, "revit_model.glb")}",
                    "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("❌ Lỗi khi export: " + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
                lblStatus.Text = "Lỗi export";
            }
            finally
            {
                btnExportGLB.IsEnabled = true;
            }
        }

        //SỬA ĐỂ NÂNG CẤP SAU
        //private async void btnOnlineWatch_Click(object sender, RoutedEventArgs e)
        //{
        //    try
        //    {
        //        btnOnlineWatch.IsEnabled = false;
        //        lblStatus.Text = "Đang chuẩn bị dữ liệu...";

        //        var stream = OpenUI.LastStream;
        //        if (stream == null || stream.objects == null || stream.objects.Count == 0)
        //        {
        //            MessageBox.Show("Chưa có dữ liệu để xem. Vui lòng nhấn 'Collect' trước.",
        //                "OnlyWatch", MessageBoxButton.OK, MessageBoxImage.Warning);
        //            lblStatus.Text = "Không có dữ liệu để xem.";
        //            return;
        //        }

        //        // 🟢 Bước 1: Xuất lại JSON + GLB mới nhất
        //        string exportFolder = Path.Combine(
        //            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        //            "RevitExports"
        //        );
        //        Directory.CreateDirectory(exportFolder);

        //        lblStatus.Text = "Đang xuất file JSON và GLB...";
        //        await Task.Run(() => ExportService.ExportJsonAndGlb(stream, exportFolder));

        //        // 🟢 Bước 2: Upload cả JSON + GLB lên Flask
        //        lblStatus.Text = "Đang upload lên Flask...";
        //        string flaskBase = "http://127.0.0.1:5000";
        //        string viewerUrl = await UploadJsonAndGlbAsync(flaskBase, exportFolder);

        //        // 🟢 Bước 3: Mở viewer trong trình duyệt
        //        Process.Start(new ProcessStartInfo(viewerUrl) { UseShellExecute = true });
        //        lblStatus.Text = "Đã mở viewer thành công.";
        //    }
        //    catch (Exception ex)
        //    {
        //        MessageBox.Show("❌ Lỗi khi xem online: " + ex.Message,
        //            "OnlyWatch", MessageBoxButton.OK, MessageBoxImage.Error);
        //        lblStatus.Text = "Lỗi xem online.";
        //    }
        //    finally
        //    {
        //        btnOnlineWatch.IsEnabled = true;
        //    }
        //}


        // (Hai hàm UploadGeoJson... giữ nguyên không thay đổi)
        private static async Task<string> UploadGeoJsonToFlaskAsync(string flaskBaseUrl, string geojson)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var content = new StringContent(geojson, Encoding.UTF8, "application/json");
            var url = $"{flaskBaseUrl.TrimEnd('/')}/upload";
            var res = await http.PostAsync(url, content);
            var body = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
                throw new Exception($"Flask upload failed ({(int)res.StatusCode}): {body}");

            dynamic obj = JsonConvert.DeserializeObject(body);
            string viewerUrl = (string)(obj?.viewer_url ?? obj?.url);

            if (string.IsNullOrWhiteSpace(viewerUrl))
                throw new Exception("Flask trả về nhưng không có trường 'viewer_url' (body: " + body + ")");

            if (viewerUrl.StartsWith("/"))
                viewerUrl = flaskBaseUrl.TrimEnd('/') + viewerUrl;

            return viewerUrl;
        }

        private static async Task<string> UploadGeoJsonMultipartAsync(string flaskBaseUrl, string geojson)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(geojson)), "file", "revit_model.json");

            var url = $"{flaskBaseUrl.TrimEnd('/')}/upload";
            var res = await http.PostAsync(url, form);
            var body = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
                throw new Exception($"Flask upload (multipart) failed ({(int)res.StatusCode}): {body}");

            dynamic obj = JsonConvert.DeserializeObject(body);
            string viewerUrl = (string)(obj?.viewer_url ?? obj?.url);
            if (string.IsNullOrWhiteSpace(viewerUrl))
                throw new Exception("Flask (multipart) không trả 'viewer_url' (body: " + body + ")");

            if (viewerUrl.StartsWith("/"))
                viewerUrl = flaskBaseUrl.TrimEnd('/') + viewerUrl;

            return viewerUrl;
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
                throw new Exception("Flask không trả về 'viewer_url' (body: " + body + ")");

            if (viewerUrl.StartsWith("/"))
                viewerUrl = flaskBaseUrl.TrimEnd('/') + viewerUrl;

            return viewerUrl;
        }


    }
}