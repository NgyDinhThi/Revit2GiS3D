using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitToGISsupport
{
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class ExportToGIS : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                Document doc = commandData.Application.ActiveUIDocument.Document;


                var window = new UI.MainWindows();
                window.ShowDialog();

                return Result.Succeeded;
            }
            catch (System.Exception ex)
            {
                TaskDialog.Show("Error", "❌ Lỗi khi mở UI: " + ex.Message);
                return Result.Failed;
            }
        }
    }
}
