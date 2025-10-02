using Autodesk.Revit.UI;
using System;
using System.IO;
using System.Windows;
using RevitToGISsupport.Models;

namespace RevitToGISsupport
{
    public static class OpenUI
    {
        // Lưu lại ExternalCommandData để UI có thể truy cập nếu cần
        public static ExternalCommandData CmdData { get; set; }

        // Lưu lại stream được thu thập gần nhất (nếu ExportToGIS đã thu thập)
        public static GISStream LastStream { get; set; }

        // Mở cửa sổ UI chính (MainWindows nằm trong thư mục UI)
        public static void ShowMainUI()
        {
            try
            {
                var window = new UI.MainWindows();
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Không thể mở giao diện: " + ex.Message, "Lỗi UI", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Lưu log gửi dữ liệu
        public static void SaveSendHistory(string user, string status, Exception ex = null)
        {
            try
            {
                string logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "history_log.txt"
                );

                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {user} | {status}";
                File.AppendAllLines(logPath, new[] { line });

                if (ex != null)
                {
                    File.AppendAllLines(logPath, new[] { $"Chi tiết lỗi: {ex}" });
                }
            }
            catch (Exception logEx)
            {
                MessageBox.Show("Không thể ghi log: " + logEx.Message, "Lỗi log", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
