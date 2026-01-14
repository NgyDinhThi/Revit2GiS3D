using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitToGISsupport.DataTree;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RevitToGISsupport.UI
{
    public partial class BrowserWindow : Window
    {
        private readonly UIApplication _uiapp;
        private readonly Document _doc;
        private readonly ExternalEvent _activateEvent;

        private BrowserNode _viewsRoot;
        private BrowserNode _sheetsRoot;
        private BrowserNode _schedulesRoot;

        public BrowserWindow(UIApplication uiapp, ExternalEvent activateEvent)
        {
            InitializeComponent();

            _uiapp = uiapp;
            _doc = uiapp?.ActiveUIDocument?.Document;
            _activateEvent = activateEvent;

            Loaded += BrowserWindow_Loaded;
        }

        private void BrowserWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_doc == null)
            {
                MessageBox.Show("Không lấy được Document.", "Browser", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
                return;
            }

            BuildAllTrees();
            BindAllTrees();
        }

        private void BuildAllTrees()
        {
            _viewsRoot = BuildViewsTree(_doc);
            _sheetsRoot = BuildSheetsTree(_doc);
            _schedulesRoot = BuildSchedulesTree(_doc);
        }

        private void BindAllTrees()
        {
            tvViews.ItemsSource = new[] { _viewsRoot };
            tvSheets.ItemsSource = new[] { _sheetsRoot };
            tvSchedules.ItemsSource = new[] { _schedulesRoot };
        }

        private BrowserNode BuildViewsTree(Document doc)
        {
            var org = BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc);
            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .Cast<Element>()
                .ToList();

            return BrowserTreeBuilder.BuildTree(
                doc,
                "Views",
                views,
                org,
                e => (e as View)?.Name ?? e.Name);
        }

        private BrowserNode BuildSheetsTree(Document doc)
        {
            var org = BrowserOrganization.GetCurrentBrowserOrganizationForSheets(doc);
            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsTemplate)
                .Cast<Element>()
                .ToList();

            return BrowserTreeBuilder.BuildTree(
                doc,
                "Sheets",
                sheets,
                org,
                e =>
                {
                    var s = e as ViewSheet;
                    if (s == null) return e.Name;
                    return $"{s.SheetNumber} - {s.Name}";
                });
        }

        private BrowserNode BuildSchedulesTree(Document doc)
        {
            var org = BrowserOrganization.GetCurrentBrowserOrganizationForSchedules(doc);
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(v => !v.IsTemplate)
                .Cast<Element>()
                .ToList();

            var root = BrowserTreeBuilder.BuildTree(
                doc,
                "Schedules",
                schedules,
                org,
                e => (e as ViewSchedule)?.Name ?? e.Name);

            // Optional: Panel schedules (nếu có) - để an toàn dùng reflection
            TryAppendPanelSchedules(doc, root);

            return root;
        }

        private void TryAppendPanelSchedules(Document doc, BrowserNode schedulesRoot)
        {
            try
            {
                var panelScheduleType = Type.GetType("Autodesk.Revit.DB.Electrical.PanelScheduleView, RevitAPI");
                if (panelScheduleType == null) return;

                var mi = typeof(BrowserOrganization).GetMethod(
                    "GetCurrentBrowserOrganizationForPanelSchedules",
                    BindingFlags.Public | BindingFlags.Static);

                if (mi == null) return;

                var org = mi.Invoke(null, new object[] { doc }) as BrowserOrganization;
                if (org == null) return;

                var panels = new FilteredElementCollector(doc)
                    .OfClass(panelScheduleType)
                    .Cast<Element>()
                    .ToList();

                if (panels.Count == 0) return;

                var subTree = BrowserTreeBuilder.BuildTree(
                    doc,
                    "Panel Schedules",
                    panels,
                    org,
                    e => (e as View)?.Name ?? e.Name);

                schedulesRoot.Children.Add(subTree);
            }
            catch
            {
                // ignore
            }
        }

        private void btnRefresh_Click(object sender, RoutedEventArgs e)
        {
            BuildAllTrees();
            ApplySearch();
        }

        private void tbSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplySearch();
        }

        private void ApplySearch()
        {
            var q = (tbSearch.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(q))
            {
                BindAllTrees();
                return;
            }

            tvViews.ItemsSource = new[] { FilterTree(_viewsRoot, q) }.Where(x => x != null).ToList();
            tvSheets.ItemsSource = new[] { FilterTree(_sheetsRoot, q) }.Where(x => x != null).ToList();
            tvSchedules.ItemsSource = new[] { FilterTree(_schedulesRoot, q) }.Where(x => x != null).ToList();
        }

        private BrowserNode FilterTree(BrowserNode node, string q)
        {
            if (node == null) return null;

            if (node.IsLeaf)
            {
                if ((node.Title ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    return node;
                return null;
            }

            var copy = new BrowserNode { Title = node.Title, Type = node.Type, ElementId = node.ElementId };
            foreach (var c in node.Children)
            {
                var fc = FilterTree(c, q);
                if (fc != null) copy.Children.Add(fc);
            }

            return copy.Children.Count > 0 ? copy : null;
        }

        private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var tv = sender as TreeView;
            var node = tv?.SelectedItem as BrowserNode;
            if (node == null) return;
            if (!node.IsLeaf) return;

            ActivateRequest.Set(node.ElementId);
            _activateEvent?.Raise();
        }
    }
}
