using Autodesk.Revit.DB;
using System.Collections.ObjectModel;

namespace RevitToGISsupport.DataTree
{
    public enum BrowserNodeType
    {
        Folder = 0,
        Item = 1
    }

    public sealed class BrowserNode
    {
        public string Title { get; set; }
        public BrowserNodeType Type { get; set; }
        public ElementId ElementId { get; set; } = ElementId.InvalidElementId;
        public ObservableCollection<BrowserNode> Children { get; } = new ObservableCollection<BrowserNode>();

        public bool IsLeaf => Type == BrowserNodeType.Item;
    }
}
