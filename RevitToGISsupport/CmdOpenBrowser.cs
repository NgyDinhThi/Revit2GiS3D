using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using RevitToGISsupport.DataTree;
using RevitToGISsupport.UI;

namespace RevitToGISsupport
{
    [Transaction(TransactionMode.Manual)]
    public class CmdOpenBrowser : IExternalCommand
    {
        private static BrowserManager _manager;

        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            if (_manager == null)
                _manager = new BrowserManager();

            _manager.ShowWindow(commandData.Application);
            return Result.Succeeded;
        }
    }

    /// <summary>
    /// Quản lý singleton cho BrowserWindow để tránh memory leak
    /// </summary>
    internal class BrowserManager
    {
        private ExternalEvent _activateEvent;
        private ActivateItemHandler _activateHandler;
        private BrowserWindow _window;

        public void ShowWindow(UIApplication uiapp)
        {
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
        }
    }
}