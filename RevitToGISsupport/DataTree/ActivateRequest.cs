using Autodesk.Revit.DB;

namespace RevitToGISsupport.DataTree
{
    public static class ActivateRequest
    {
        public static ElementId PendingElementId { get; private set; } = ElementId.InvalidElementId;

        public static void Set(ElementId id)
        {
            PendingElementId = id ?? ElementId.InvalidElementId;
        }

        public static ElementId Consume()
        {
            var id = PendingElementId;
            PendingElementId = ElementId.InvalidElementId;
            return id;
        }
    }
}
