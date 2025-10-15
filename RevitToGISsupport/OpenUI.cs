using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using RevitToGISsupport.Models;
using RevitToGISsupport.Services;
using System;
using System.Threading.Tasks;

namespace RevitToGISsupport
{
    /// <summary>
    /// OpenUI: tạo ExternalEvent để collect dữ liệu trên Revit thread,
    /// expose Progress reporter (optional), lưu LastStream và mở MainWindow modeless.
    /// </summary>
    public static class OpenUI
    {
        // ExternalEvent + handler instance
        public static ExternalEvent CollectEvent { get; private set; }
        public static IExternalEventHandler HandlerInstance { get; private set; }

        // UI (MainWindow) có thể set để nhận % tiến độ. ExportService bản hiện tại không hỗ trợ,
        // nên bé chỉ report 0% lúc bắt đầu và 100% khi xong cho đỡ “cô đơn”.
        public static IProgress<int> ProgressReporter { get; set; }

        // UI sẽ await cái này để biết khi nào collect xong
        public static TaskCompletionSource<bool> CollectTcs;

        // Kết quả sau khi collect
        public static GISStream LastStream { get; set; }

        /// <summary>Gọi 1 lần từ ExternalCommand.Execute</summary>
        public static void Initialize()
        {
            if (HandlerInstance == null)
            {
                HandlerInstance = new CollectHandler();
                CollectEvent = ExternalEvent.Create(HandlerInstance);
            }
        }

        /// <summary>Mở WPF window modeless để UI có thể Raise ExternalEvent</summary>
        public static void ShowMainUI()
        {
            try
            {
                var window = new UI.MainWindows();
                window.Show(); // modeless
            }
            catch (Exception ex)
            {
                TaskDialog.Show("OpenUI", "Cannot open UI: " + ex.Message);
            }
        }

        /// <summary>
        /// Handler chạy trên Revit thread. KHỚP với ExportService.CollectData(Document doc) (không progress).
        /// </summary>
        private class CollectHandler : IExternalEventHandler
        {
            public void Execute(UIApplication app)
            {
                try
                {
                    // báo “fake” 0% để UI có tín hiệu
                    try { ProgressReporter?.Report(0); } catch { }

                    var doc = app?.ActiveUIDocument?.Document;
                    if (doc != null)
                    {
                        // ✨ KHÔNG truyền progress nữa vì ExportService hiện tại chỉ nhận 1 tham số
                        LastStream = ExportService.CollectData(doc);
                    }

                    // xong việc → 100%
                    try { ProgressReporter?.Report(100); } catch { }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("CollectHandler error: " + ex);
                }
                finally
                {
                    // báo cho UI biết đã xong (kể cả có lỗi)
                    CollectTcs?.TrySetResult(true);
                }
            }

            public string GetName() => "CollectDataHandler";
        }
    }
}
