using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitToGISsupport;
using System;
using System.Windows;

namespace RevitToGISsupport
{
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class ExportToGIS : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // Khởi tạo ExternalEvent
                OpenUI.Initialize();

                // (option) lưu UIApplication nếu cần: OpenUI.AppData = commandData; // nếu anh muốn
                // Mở UI (modeless or dialog). Nếu dùng ShowDialog() và UI có owner mismatch, thay bằng window.Show();
                OpenUI.ShowMainUI();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", ex.Message);
                return Result.Failed;
            }
        }

    }
}
