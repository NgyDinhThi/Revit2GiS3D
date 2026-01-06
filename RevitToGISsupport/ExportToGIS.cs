using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace RevitToGISsupport
{
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class ExportToGIS : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                OpenUI.Initialize();

                // Quan trọng: cache UIApplication/Document ngay khi chạy command
                OpenUI.SetContext(commandData.Application);

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
