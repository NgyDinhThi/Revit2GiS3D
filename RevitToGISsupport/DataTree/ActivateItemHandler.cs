using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;

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

            var elem = doc.GetElement(id);
            if (elem == null) return;

            // Nếu là View (bao gồm Sheet/Schedule) thì activate
            if (elem is View v && !v.IsTemplate)
            {
                try
                {
                    uidoc.ActiveView = v;
                    return;
                }
                catch
                {
                    TaskDialog.Show("Activate", "Không thể activate view này.");
                    return;
                }
            }

            // Nếu không phải View: select element (FamilySymbol/GroupType/RevitLinkInstance...)
            try
            {
                uidoc.Selection.SetElementIds(new List<ElementId> { id });
                uidoc.ShowElements(id);
            }
            catch
            {
                // ignore
            }
        }

        public string GetName() => "ActivateItemHandler";
    }
}
