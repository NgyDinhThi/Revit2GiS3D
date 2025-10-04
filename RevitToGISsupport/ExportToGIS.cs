using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitToGISsupport.Services;
using System;
using System.IO;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace RevitToGISsupport
{
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class ExportToGIS : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // Lưu commandData nếu UI cần (không bắt buộc)
                OpenUI.CmdData = commandData;

                Document doc = commandData.Application.ActiveUIDocument.Document;

                // 1. Thu thập dữ liệu từ model (không hỏi folder ở đây)
                var stream = ExportService.CollectData(doc);

                // 2. Lưu stream để UI dùng (UI sẽ làm phần chọn folder & xuất file)
                OpenUI.LastStream = stream;

                // 3. Mở UI để người dùng tương tác (UI sẽ hỏi folder khi họ bấm nút Export)
                OpenUI.ShowMainUI();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                // Hiện lỗi cho user (kèm stacktrace lúc debug)
                TaskDialog.Show("Export to GIS - Error", "❌ Lỗi khi thu thập dữ liệu: " + ex.Message + "\n\nXem Output/Debug để chi tiết.");
                System.Diagnostics.Debug.WriteLine(ex.ToString());
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
