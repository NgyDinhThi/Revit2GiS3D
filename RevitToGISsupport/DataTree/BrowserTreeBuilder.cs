using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitToGISsupport.DataTree
{
    public static class BrowserTreeBuilder
    {
        public static BrowserNode BuildTree(
            Document doc,
            string rootTitle,
            IEnumerable<Element> elements,
            BrowserOrganization org,
            Func<Element, string> getItemTitle)
        {
            var root = new BrowserNode
            {
                Title = rootTitle,
                Type = BrowserNodeType.Folder,
                ElementId = ElementId.InvalidElementId
            };

            if (doc == null || elements == null) return root;

            foreach (var e in elements)
            {
                if (e == null) continue;

                if (e is View v && v.IsTemplate) continue;

                if (org != null && !org.AreFiltersSatisfied(e.Id))
                    continue;

                var path = GetFolderPath(org, e.Id);
                if (path.Count == 0) path.Add("Ungrouped");

                Insert(root, path, e.Id, getItemTitle(e));
            }

            SortTree(root);
            return root;
        }

        private static List<string> GetFolderPath(BrowserOrganization org, ElementId id)
        {
            var path = new List<string>();
            if (org == null || id == null) return path;

            var infos = org.GetFolderItems(id);
            if (infos == null) return path;

            foreach (var info in infos)
            {
                var name = info?.Name;
                if (!string.IsNullOrWhiteSpace(name))
                    path.Add(name.Trim());
            }

            return path;
        }

        private static void Insert(BrowserNode root, List<string> folderPath, ElementId elementId, string title)
        {
            var current = root;

            foreach (var folder in folderPath)
            {
                var next = current.Children.FirstOrDefault(x => x.Type == BrowserNodeType.Folder && x.Title == folder);
                if (next == null)
                {
                    next = new BrowserNode
                    {
                        Title = folder,
                        Type = BrowserNodeType.Folder,
                        ElementId = ElementId.InvalidElementId
                    };
                    current.Children.Add(next);
                }
                current = next;
            }

            current.Children.Add(new BrowserNode
            {
                Title = title,
                Type = BrowserNodeType.Item,
                ElementId = elementId
            });
        }

        private static void SortTree(BrowserNode node)
        {
            var ordered = node.Children
                .OrderBy(x => x.Type)
                .ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            node.Children.Clear();
            foreach (var c in ordered) node.Children.Add(c);

            foreach (var c in node.Children)
                SortTree(c);
        }
    }
}
