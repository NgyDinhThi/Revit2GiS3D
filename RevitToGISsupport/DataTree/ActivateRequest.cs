using Autodesk.Revit.DB;

namespace RevitToGISsupport.DataTree
{
    public static class ActivateRequest
    {
        private static readonly object _lock = new object();
        private static ElementId _pendingElementId = ElementId.InvalidElementId;

        public static void Set(ElementId id)
        {
            lock (_lock)
            {
                _pendingElementId = id ?? ElementId.InvalidElementId;
            }
        }

        public static ElementId Consume()
        {
            lock (_lock)
            {
                var id = _pendingElementId;
                _pendingElementId = ElementId.InvalidElementId;
                return id;
            }
        }
    }
}
