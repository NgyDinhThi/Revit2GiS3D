using RevitToGISsupport.Models;
using RevitToGISsupport.Services;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;

namespace RevitToGISsupport.UI
{
    public partial class MainWindows : Window
    {
        public MainWindows()
        {
            InitializeComponent();
        }

        // btnCollect_Click: request Revit to collect using ExternalEvent, await completion
        private async void btnCollect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                btnCollect.IsEnabled = false;
                lblStatus.Text = "Collecting data from Revit..."; // nếu lblStatus là TextBlock dùng Text; nếu là Label dùng Content

                // chuẩn bị progress reporter (UI thread)
                progressBar.Visibility = Visibility.Visible;
                progressBar.Value = 0;
                progressBar.Maximum = 100;

                OpenUI.ProgressReporter = new Progress<int>(pct =>
                {
                    // đảm bảo chạy trên UI thread
                    if (pct < 0) pct = 0;
                    if (pct > 100) pct = 100;
                    progressBar.Value = pct;
                    lblStatus.Text = $"Collecting... {pct}%";
                });

                // chuẩn bị TCS
                OpenUI.CollectTcs = new TaskCompletionSource<bool>();

                // raise external event (handler chạy trên Revit thread)
                if (OpenUI.CollectEvent == null)
                {
                    MessageBox.Show("ExternalEvent chưa được khởi tạo. Hãy chạy command để mở UI (nếu chưa).", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                OpenUI.CollectEvent.Raise();

                // chờ hoàn thành hoặc timeout lâu hơn (ví dụ 10 phút)
                var completedTask = await Task.WhenAny(OpenUI.CollectTcs.Task, Task.Delay(TimeSpan.FromMinutes(10)));
                if (completedTask != OpenUI.CollectTcs.Task)
                {
                    MessageBox.Show("⏱️ Collect timed out (10 phút). Check Revit or try again.", "Timeout", MessageBoxButton.OK, MessageBoxImage.Warning);
                    lblStatus.Text = "Collect timed out";
                    return;
                }

                // Done
                var stream = OpenUI.LastStream;
                if (stream == null || stream.objects == null || stream.objects.Count == 0)
                {
                    MessageBox.Show("⚠️ Không thu được dữ liệu từ Revit (stream rỗng).", "Collect", MessageBoxButton.OK, MessageBoxImage.Warning);
                    lblStatus.Text = "No data collected";
                }
                else
                {
                    lblStatus.Text = $"Collected {stream.objects.Count} objects";
                    MessageBox.Show($"✅ Đã thu thập {stream.objects.Count} đối tượng từ Revit.", "Collect", MessageBoxButton.OK, MessageBoxImage.Information);

                    // lưu bản copy nhanh ở Documents
                    try
                    {
                        string exportFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports");
                        Directory.CreateDirectory(exportFolder);
                        string jsonPath = Path.Combine(exportFolder, "revit_model.json");
                        File.WriteAllText(jsonPath, JsonConvert.SerializeObject(stream.ToGeoJson(), Formatting.Indented));
                    }
                    catch { /* ignore IO errors */ }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi khi collect: " + ex.Message, "Collect", MessageBoxButton.OK, MessageBoxImage.Error);
                lblStatus.Text = "Collect error";
            }
            finally
            {
                // reset
                progressBar.Visibility = Visibility.Collapsed;
                progressBar.Value = 0;
                OpenUI.ProgressReporter = null;
                btnCollect.IsEnabled = true;
            }
        }



        // btnExportGLB_Click: runs ExportJsonAndGlb on background thread (safe)
        private async void btnExportGLB_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // prefer in-memory stream if we collected earlier
                GISStream stream = OpenUI.LastStream;

                // else try load from Documents/RevitExports
                if (stream == null)
                {
                    string defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports");
                    string jsonPath = Path.Combine(defaultFolder, "revit_model.json");
                    if (File.Exists(jsonPath))
                    {
                        stream = JsonConvert.DeserializeObject<GISStream>(File.ReadAllText(jsonPath));
                    }
                }

                if (stream == null)
                {
                    MessageBox.Show("❌ Chưa có dữ liệu để export. Hãy Collect trước hoặc chắc rằng revit_model.json tồn tại trong Documents/RevitExports.", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // choose folder
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
                lblStatus.Text = "Exporting (this may take some time)...";

                await Task.Run(() =>
                {
                    ExportService.ExportJsonAndGlb(stream, folderPath);
                });

                lblStatus.Text = "Export finished";
                MessageBox.Show($"✅ Đã export xong:\nJSON: {Path.Combine(folderPath, "revit_model.json")}\nGLB:  {Path.Combine(folderPath, "revit_model.glb")}", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("❌ Lỗi khi export: " + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
                lblStatus.Text = "Export error";
            }
            finally
            {
                btnExportGLB.IsEnabled = true;
            }
        }


        private async void btnSendSpeckle_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                btnSendSpeckle.IsEnabled = false;
                lblStatus.Text = "Uploading to Speckle...";

                // load stream (ưu tiên OpenUI.LastStream nếu có)
                GISStream stream = OpenUI.LastStream;
                if (stream == null)
                {
                    string exportFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitExports");
                    string jsonPath = Path.Combine(exportFolder, "revit_model.json");
                    if (!File.Exists(jsonPath))
                    {
                        MessageBox.Show("❌ Không tìm thấy revit_model.json trong Documents\\RevitExports. Hãy collect hoặc export trước.", "Speckle Upload", MessageBoxButton.OK, MessageBoxImage.Warning);
                        lblStatus.Text = "No data to send";
                        return;
                    }
                    stream = JsonConvert.DeserializeObject<GISStream>(File.ReadAllText(jsonPath));
                }

                // config speckle (thay token nếu cần)
                string serverUrl = "https://speckle.xyz";
                string token = "YOUR_SPECKLE_TOKEN";
                // gọi uploader (giữ await như file SpeckleUploader)
                await SpeckleUploader.SendStream(stream, serverUrl, token);

                lblStatus.Text = "Speckle upload finished";
                MessageBox.Show("✅ Đã gửi lên Speckle thành công.", "Speckle", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Speckle upload error";
                MessageBox.Show("❌ Lỗi khi gửi lên Speckle: " + ex.Message, "Speckle", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnSendSpeckle.IsEnabled = true;
            }
        }


    }
}
