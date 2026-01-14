using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitToGISsupport.DataTree
{
    public sealed class ActivateItemHandler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            var id = ActivateRequest.Consume();
            if (id == null || id == ElementId.InvalidElementId) return;

            var uidoc = app?.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return;

            var view = doc.GetElement(id) as View;
            if (view == null || view.IsTemplate) return;

            try
            {
                uidoc.ActiveView = view;
            }
            catch
            {
                TaskDialog.Show("Activate", "Không thể activate view này.");
            }
        }

        public string GetName() => "ActivateItemHandler";
    }
}
