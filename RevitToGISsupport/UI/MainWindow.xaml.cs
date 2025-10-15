using Newtonsoft.Json;
using RevitToGISsupport.Models;
using RevitToGISsupport.Services;
using RevitToGISsupport.Utils;
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
            try
            {
                btnCollect.IsEnabled = false;
                lblStatus.Text = "Đang thu thập dữ liệu từ Revit...";

                // show progress
                progressBar.Visibility = Visibility.Visible;
                progressBar.Value = 0;
                progressBar.Maximum = 100;

                // reporter từ Revit-thread về UI
                OpenUI.ProgressReporter = new Progress<int>(pct =>
                {
                    if (pct < 0) pct = 0;
                    if (pct > 100) pct = 100;
                    progressBar.Value = pct;
                    lblStatus.Text = $"Đang thu thập... {pct}%";
                });

                // chờ kết thúc
                OpenUI.CollectTcs = new TaskCompletionSource<bool>();

                if (OpenUI.CollectEvent == null)
                {
                    MessageBox.Show("ExternalEvent chưa được khởi tạo. Hãy mở UI từ lệnh Revit trước.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                OpenUI.CollectEvent.Raise();

                // timeout 10 phút
                var completed = await Task.WhenAny(OpenUI.CollectTcs.Task, Task.Delay(TimeSpan.FromMinutes(10)));
                if (completed != OpenUI.CollectTcs.Task)
                {
                    MessageBox.Show("⏱️ Hết thời gian chờ (10 phút). Vui lòng kiểm tra Revit rồi thử lại.", "Timeout",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    lblStatus.Text = "Hết thời gian chờ";
                    return;
                }

                var stream = OpenUI.LastStream;
                if (stream == null || stream.objects == null || stream.objects.Count == 0)
                {
                    MessageBox.Show("⚠️ Không thu được dữ liệu (stream rỗng).", "Collect",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    lblStatus.Text = "Không có dữ liệu";
                }
                else
                {
                    lblStatus.Text = $"Đã thu thập {stream.objects.Count} đối tượng";

                    // lưu nhanh để xem/backup
                    try
                    {
                        var exportFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports");
                        Directory.CreateDirectory(exportFolder);
                        var jsonPath = Path.Combine(exportFolder, "revit_model.json");
                        File.WriteAllText(jsonPath, JsonConvert.SerializeObject(stream.ToGeoJson(), Formatting.Indented));
                    }
                    catch { /* ignore IO */ }

                    MessageBox.Show($"✅ Thu thập xong: {stream.objects.Count} đối tượng.", "Collect",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi khi thu thập: " + ex.Message, "Collect",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                lblStatus.Text = "Lỗi thu thập";
            }
            finally
            {
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

                if (stream == null)
                {
                    string defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports");
                    string gzPath = Path.Combine(defaultFolder, "revit_model.json.gz");
                    string jsonPath = File.Exists(gzPath) ? gzPath : Path.Combine(defaultFolder, "revit_model.json");
                    if (File.Exists(jsonPath))
                    {
                        stream = await JsonLoader.LoadStreamAsync(jsonPath);
                    }
                }

                if (stream == null)
                {
                    MessageBox.Show("❌ Chưa có dữ liệu để export. Hãy Collect trước hoặc đặt revit_model.json(.gz) vào Documents\\RevitExports.",
                        "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string folderPath = null;
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dlg.Description = "Chọn thư mục để lưu revit_model.json(.gz) và revit_model.glb";
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
                    $"✅ Đã xuất xong:\n- JSON: {Path.Combine(folderPath, "revit_model.json")}\n- JSON nén: {Path.Combine(folderPath, "revit_model.json.gz")}\n- GLB: {Path.Combine(folderPath, "revit_model.glb")}",
                    "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("❌ Lỗi khi export: " + ex.Message, "Export",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                lblStatus.Text = "Lỗi export";
            }
            finally
            {
                btnExportGLB.IsEnabled = true;
            }
        }

        // ==========================
        //  ONLY WATCH (FLASK)
        // ==========================
        private OnlineWatchServer _onlineWatch; // nếu dùng chế độ server nội bộ

        // Gửi RAW JSON (optional gzip)
        private static async Task<string> UploadGeoJsonToFlaskAsync(string flaskBaseUrl, string geojson, bool gzipIt = false)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            HttpContent content;

            if (gzipIt)
            {
                var bytes = Encoding.UTF8.GetBytes(geojson);
                var ms = new MemoryStream();
                using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
                    gz.Write(bytes, 0, bytes.Length);
                ms.Position = 0;

                var sc = new StreamContent(ms);
                sc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                sc.Headers.ContentEncoding.Add("gzip");
                content = sc;
            }
            else
            {
                content = new StringContent(geojson, Encoding.UTF8, "application/json");
            }

            var url = $"{flaskBaseUrl.TrimEnd('/')}/upload";
            var res = await http.PostAsync(url, content);
            var body = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
                throw new Exception($"Flask upload failed ({(int)res.StatusCode}): {body}");

            dynamic obj = JsonConvert.DeserializeObject(body);
            string viewerUrl = null;

            if (obj != null)
            {
                viewerUrl = (string)(obj.viewer ?? obj.viewer_url ?? obj.url);
                if (string.IsNullOrWhiteSpace(viewerUrl))
                {
                    var jsonPath = (string)(obj.json ?? obj.path);
                    if (!string.IsNullOrWhiteSpace(jsonPath))
                        viewerUrl = $"/viewer?file={jsonPath}";
                }
            }

            if (string.IsNullOrWhiteSpace(viewerUrl))
                throw new Exception("Flask trả về nhưng không có trường 'viewer' (body: " + body + ")");

            if (viewerUrl.StartsWith("/"))
                viewerUrl = flaskBaseUrl.TrimEnd('/') + viewerUrl;

            return viewerUrl;
        }

        // Fallback: gửi multipart/form-data
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
            string viewerUrl = (string)(obj?.viewer ?? obj?.viewer_url ?? obj?.url);
            if (string.IsNullOrWhiteSpace(viewerUrl))
                throw new Exception("Flask (multipart) không trả 'viewer' (body: " + body + ")");

            if (viewerUrl.StartsWith("/"))
                viewerUrl = flaskBaseUrl.TrimEnd('/') + viewerUrl;

            return viewerUrl;
        }

        private async void btnOnlineWatch_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                btnOnlineWatch.IsEnabled = false;
                lblStatus.Text = "Đang upload lên Flask...";

                // Lấy stream
                var stream = OpenUI.LastStream;
                if (stream == null)
                {
                    var exportFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports");
                    var gz = Path.Combine(exportFolder, "revit_model.json.gz");
                    var js = File.Exists(gz) ? gz : Path.Combine(exportFolder, "revit_model.json");
                    if (!File.Exists(js))
                    {
                        MessageBox.Show("Không có dữ liệu để xem.", "OnlyWatch");
                        return;
                    }
                    stream = await JsonLoader.LoadStreamAsync(js);
                }

                // Serialize GeoJSON
                var geojson = JsonConvert.SerializeObject(stream.ToGeoJson(), Formatting.None);

                // Upload → mở viewer
                string flaskBase = "http://127.0.0.1:5000";
                string viewerUrl;

                try
                {
                    // thử RAW trước
                    viewerUrl = await UploadGeoJsonToFlaskAsync(flaskBase, geojson, gzipIt: false);
                }
                catch (Exception exRaw)
                {
                    // fallback multipart
                    try
                    {
                        viewerUrl = await UploadGeoJsonMultipartAsync(flaskBase, geojson);
                    }
                    catch (Exception exMp)
                    {
                        throw new Exception("Upload thất bại.\nRAW: " + exRaw.Message + "\nMULTIPART: " + exMp.Message);
                    }
                }

                // Mở trình duyệt
                Process.Start(new ProcessStartInfo(viewerUrl) { UseShellExecute = true });
                lblStatus.Text = "Đã mở viewer";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi OnlyWatch (Flask): " + ex.Message, "OnlyWatch");
                lblStatus.Text = "OnlyWatch error";
            }
            finally
            {
                btnOnlineWatch.IsEnabled = true;
            }
        }

        // ==========================
        //  (OPTIONAL) VIEWER NỘI BỘ
        //  Nếu muốn dùng viewer.html nội bộ thay vì Flask,
        //  tạo OnlineWatchServer với file viewer.html & geojson:
        // ==========================
        private void OpenLocalViewer(string geojson)
        {
            // tìm viewer.html cạnh DLL hoặc Assets\viewer.html
            var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var viewerPath = Path.Combine(asmDir!, "Assets", "viewer.html");
            if (!File.Exists(viewerPath)) viewerPath = Path.Combine(asmDir!, "viewer.html");
            if (!File.Exists(viewerPath)) throw new FileNotFoundException("Không thấy viewer.html", viewerPath);

            _onlineWatch?.Dispose();
            _onlineWatch = new OnlineWatchServer(geojson, viewerPath);
            var url = _onlineWatch.Start();
            if (!url.EndsWith("/")) url += "/";
            url += "?online=1";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }
}
