using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using RevitToGISsupport.DataTree;
using RevitToGISsupport.UI;

namespace RevitToGISsupport
{
    [Transaction(TransactionMode.Manual)]
    public class CmdOpenBrowser : IExternalCommand
    {
        private static ExternalEvent _activateEvent;
        private static ActivateItemHandler _activateHandler;
        private static BrowserWindow _window;

        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            var uiapp = commandData.Application;

            if (_activateHandler == null)
            {
                _activateHandler = new ActivateItemHandler();
                _activateEvent = ExternalEvent.Create(_activateHandler);
            }

            if (_window == null || !_window.IsVisible)
            {
                _window = new BrowserWindow(uiapp, _activateEvent);
                _window.Show();
            }
            else
            {
                _window.Activate();
            }

            return Result.Succeeded;
        }
    }
}
