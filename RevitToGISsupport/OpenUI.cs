using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace RevitToGISsupport
{
    public static class OpenUI
    {
        public static UIApplication UiApp { get; private set; }
        public static Document ActiveDoc { get; private set; }

        // Giữ lại hàm Initialize trống để file cấu hình (App.cs) gọi không bị lỗi
        public static void Initialize()
        {
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
    
                var window = new UI.BrowserWindow(UiApp, null);
                window.Show();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("OpenUI", "Cannot open UI: " + ex.Message);
            }
        }
    }
}