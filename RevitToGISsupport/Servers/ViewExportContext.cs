using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Linq;

namespace RevitToGISsupport.Services
{
    public static class ViewExportContext
    {
        public static ElementId SelectedViewId { get; set; }

        public static View GetSelectedView(Document doc)
        {
            if (doc == null || SelectedViewId == null) return null;
            return doc.GetElement(SelectedViewId) as View;
        }

        public static List<View> GetExportableViews(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v =>
                    !v.IsTemplate &&
                    (v.ViewType == ViewType.ThreeD ||
                     v.ViewType == ViewType.Section ||
                     v.ViewType == ViewType.Elevation))
                .ToList();
        }
    }
}
