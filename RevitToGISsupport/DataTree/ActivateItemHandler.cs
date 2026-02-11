using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
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

            try
            {
                uidoc.Selection.SetElementIds(new List<ElementId> { id });
                uidoc.ShowElements(id);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Selection error: {ex.Message}");
                // Có thể bỏ comment dòng dưới nếu muốn thông báo cho user
                // TaskDialog.Show("Selection", $"Không thể chọn element: {ex.Message}");
            }
        }

        public string GetName() => "ActivateItemHandler";
    }
}