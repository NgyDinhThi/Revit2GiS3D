using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitToGISsupport.UI; // Namespace chứa BrowserWindow

namespace RevitToGISsupport
{
    [Transaction(TransactionMode.Manual)]
    public class CmdOpenBrowser : IExternalCommand
    {
        // Khai báo quản lý cửa sổ (chống rò rỉ bộ nhớ)
        private static BrowserWindow _browserWindow;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // 1. KHỞI TẠO CÔNG TẮC BÓC TÁCH SIÊU TỐC
            OpenUI.Initialize();
            OpenUI.SetContext(commandData.Application);

            // 2. MỞ CỬA SỔ
            if (_browserWindow == null)
            {
                var activateHandler = new DataTree.ActivateItemHandler();
                var activateEvent = ExternalEvent.Create(activateHandler);

                _browserWindow = new BrowserWindow(commandData.Application, activateEvent);
                _browserWindow.Closed += (s, e) => _browserWindow = null;
            }

            _browserWindow.Show();
            return Result.Succeeded;
        }
    }
}