using RevitToGISsupport.Models;
using RevitToGISsupport.Services;
using System;
using System.Windows;
using System.IO;              
using Newtonsoft.Json;        


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
                MessageBox.Show("Đang export JSON & GLB...", "Export", MessageBoxButton.OK, MessageBoxImage.Information);

                string exportFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "RevitExports"
                );
                Directory.CreateDirectory(exportFolder);

                // 🔹 Load JSON stream từ file tạm (nếu cần)
                string jsonPath = Path.Combine(exportFolder, "revit_model.json");
                GISStream stream = null;

                if (File.Exists(jsonPath))
                {
                    stream = JsonConvert.DeserializeObject<GISStream>(File.ReadAllText(jsonPath));
                }
                else
                {
                    MessageBox.Show("❌ Chưa có dữ liệu để export. Hãy chạy ExportToGIS trước.",
                        "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // ✅ Xuất JSON (ghi đè mới)
                File.WriteAllText(jsonPath, JsonConvert.SerializeObject(stream.ToGeoJson(), Formatting.Indented));

                // ✅ Xuất GLB
                string glbPath = Path.Combine(exportFolder, "revit_model.glb");
                GLBExporter.ExportToGLB(stream, glbPath);

                MessageBox.Show($"✅ Đã export xong:\nJSON: {jsonPath}\nGLB: {glbPath}",
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
