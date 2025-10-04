using RevitToGISsupport.Models;
using RevitToGISsupport.Services;
using System;
using System.Windows;
using System.IO;
using Newtonsoft.Json;
using System.Windows.Controls;

namespace RevitToGISsupport.UI
{
    public partial class MainWindows : Window
    {
        public MainWindows()
        {
            InitializeComponent();
        }

        private void btnExportGLB_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. Lấy stream: ưu tiên OpenUI.LastStream (nếu ExportToGIS đã thu thập)
                GISStream stream = OpenUI.LastStream;

                // 2. Nếu chưa có, thử load từ Documents/RevitExports/revit_model.json
                if (stream == null)
                {
                    string defaultFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "RevitExports"
                    );
                    string jsonPath = Path.Combine(defaultFolder, "revit_model.json");
                    if (File.Exists(jsonPath))
                    {
                        stream = JsonConvert.DeserializeObject<GISStream>(File.ReadAllText(jsonPath));
                    }
                }

                if (stream == null)
                {
                    MessageBox.Show(
                        "❌ Chưa có dữ liệu để export. Hãy chạy ExportToGIS (trong Revit) trước, hoặc đảm bảo file revit_model.json tồn tại trong Documents/RevitExports.",
                        "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 3. Mở FolderBrowserDialog để user chọn thư mục lưu
                string folderPath = null;
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dlg.Description = "Chọn thư mục để lưu revit_model.json và revit_model.glb";
                    dlg.ShowNewFolderButton = true;
                    // mặc định hướng tới Documents/RevitExports
                    dlg.SelectedPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "RevitExports");

                    var res = dlg.ShowDialog();
                    if (res == System.Windows.Forms.DialogResult.OK || res == System.Windows.Forms.DialogResult.Yes)
                    {
                        folderPath = dlg.SelectedPath;
                    }
                }

                // Nếu user Cancel -> thôi (không báo lỗi)
                if (string.IsNullOrWhiteSpace(folderPath))
                {
                    // optional: MessageBox.Show("Hủy export.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 4. Gọi ExportService để tạo cả JSON và GLB
                ExportService.ExportJsonAndGlb(stream, folderPath);

                // 5. Thông báo thành công
                string jsonOut = Path.Combine(folderPath, "revit_model.json");
                string glbOut = Path.Combine(folderPath, "revit_model.glb");
                MessageBox.Show($"✅ Đã export xong:\nJSON: {jsonOut}\nGLB:  {glbOut}",
                    "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("❌ Lỗi khi export: " + ex.Message,
                    "Export", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private async void btnSendSpeckle_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                MessageBox.Show("Đang đẩy dữ liệu lên Speckle...", "Export", MessageBoxButton.OK, MessageBoxImage.Information);

                // 🔹 Load JSON vừa export (ở Documents/RevitExports/revit_model.json)
                string exportFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "RevitExports"
                );
                string jsonPath = Path.Combine(exportFolder, "revit_model.json");
                var stream = JsonConvert.DeserializeObject<GISStream>(File.ReadAllText(jsonPath));

                // 🔹 Thông tin Speckle
                string serverUrl = "https://speckle.xyz"; // hoặc server nội bộ
                string token = "YOUR_SPECKLE_TOKEN";     // lấy trong tài khoản Speckle

                await SpeckleUploader.SendStream(stream, serverUrl, token);
            }
            catch (Exception ex)
            {
                MessageBox.Show("❌ Lỗi: " + ex.Message,
                    "Speckle Upload", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

    }
}
