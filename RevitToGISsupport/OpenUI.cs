using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitToGISsupport.Models;
using RevitToGISsupport.Services;
using System;
using System.Threading.Tasks;

namespace RevitToGISsupport
{
    public static class OpenUI
    {
        public static ExternalEvent CollectEvent { get; private set; }
        public static IExternalEventHandler HandlerInstance { get; private set; }

        public static TaskCompletionSource<bool> CollectTcs;

        public static GISStream LastStream { get; set; }

        public static UIApplication UiApp { get; private set; }
        public static Document ActiveDoc { get; private set; }

        public static void Initialize()
        {
            if (HandlerInstance == null)
            {
                HandlerInstance = new CollectHandler();
                CollectEvent = ExternalEvent.Create(HandlerInstance);
            }
        }

        public static void SetContext(UIApplication uiApp)
        {
            UiApp = uiApp;
            ActiveDoc = uiApp?.ActiveUIDocument?.Document;
        }

        public static void ShowMainUI()
        {
            try
            {
                var window = new UI.MainWindows();
                window.Show();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("OpenUI", "Cannot open UI: " + ex.Message);
            }
        }

        private class CollectHandler : IExternalEventHandler
        {
            public void Execute(UIApplication app)
            {
                try
                {
                    // refresh context mỗi lần collect
                    UiApp = app;
                    ActiveDoc = app?.ActiveUIDocument?.Document;

                    var doc = ActiveDoc;
                    if (doc == null) return;

                    var selectedView = ViewExportContext.GetSelectedView(doc);
                    LastStream = ExportService.CollectData(doc, selectedView);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("CollectHandler error: " + ex);
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
