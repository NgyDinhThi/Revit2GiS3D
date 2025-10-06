using Autodesk.Revit.UI;
using RevitToGISsupport.Models;
using RevitToGISsupport.Services;
using System;
using System.Threading.Tasks;

namespace RevitToGISsupport
{
    /// <summary>
    /// OpenUI: tạo ExternalEvent để collect dữ liệu trên Revit thread,
    /// expose Progress reporter, lưu LastStream và mở MainWindow modeless.
    /// </summary>
    public static class OpenUI
    {
        // ExternalEvent + handler instance (handler là private inner class)
        public static ExternalEvent CollectEvent { get; private set; }
        public static IExternalEventHandler HandlerInstance { get; private set; }

        // UI sets this before raising event so handler can report progress
        public static IProgress<int> ProgressReporter { get; set; }

        // UI awaits this to know collect finished
        public static TaskCompletionSource<bool> CollectTcs;

        // result (filled by handler running on Revit thread)
        public static GISStream LastStream { get; set; }

        // call once from ExternalCommand.Execute
        public static void Initialize()
        {
            if (HandlerInstance == null)
            {
                HandlerInstance = new CollectHandler();
                CollectEvent = ExternalEvent.Create(HandlerInstance);
            }
        }

        // Show the WPF window modeless so UI can raise ExternalEvent
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

        // single inner class handler (ONLY ONE definition here)
        private class CollectHandler : IExternalEventHandler
        {
            public void Execute(UIApplication app)
            {
                try
                {
                    var doc = app?.ActiveUIDocument?.Document;
                    if (doc != null)
                    {
                        // ExportService.CollectData should accept IProgress<int> (or ignore it)
                        LastStream = ExportService.CollectData(doc, ProgressReporter);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("CollectHandler error: " + ex.ToString());
                }
                finally
                {
                    CollectTcs?.TrySetResult(true);
                }
            }

            public string GetName() => "CollectDataHandler";
        }
    }
}
