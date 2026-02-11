using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitToGISsupport.DataTree;
using RevitToGISsupport.RemoteControl;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
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

        private BrowserNode _root;

        // FIXED: Dùng static HttpClient để tránh socket exhaustion
        private static readonly HttpClient SharedHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        // FIXED: Thêm cancellation token cho async operations
        private CancellationTokenSource _publishCts;

        // Remote (Web -> Revit)
        private static RemoteCommandHandler _remoteHandler;
        private static ExternalEvent _remoteEvent;
        private RemoteCommandPoller _poller;

        // Poller config guard
        private string _pollerServer;
        private string _pollerProjectId;
        private string _pollerClientId;

        public BrowserWindow(UIApplication uiapp, ExternalEvent activateEvent)
        {
            InitializeComponent();

            _uiapp = uiapp;
            _doc = uiapp?.ActiveUIDocument?.Document;
            _activateEvent = activateEvent;

            // FIXED: Không tạo HttpClient mới nữa

            Loaded += BrowserWindow_Loaded;
            Closed += BrowserWindow_Closed;
        }

        private void BrowserWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_doc == null)
            {
                MessageBox.Show("Không lấy được Document.", "Browser", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
                return;
            }

            // Default server/projectId để poller có thể start ngay
            if (string.IsNullOrWhiteSpace(tbServer.Text))
                tbServer.Text = "http://127.0.0.1:5000";
            if (string.IsNullOrWhiteSpace(tbProjectId.Text))
                tbProjectId.Text = "P001";

            BuildRootTree();
            BindTree();

            StartOrRestartPoller(force: true);

            lblStatus.Text = "Ready.";
        }

        private void BrowserWindow_Closed(object sender, EventArgs e)
        {
            // FIXED: Cancel và dispose cancellation token
            _publishCts?.Cancel();
            _publishCts?.Dispose();

            StopPoller();

            // FIXED: Không dispose SharedHttpClient vì nó là static
        }

        // =========================
        // UI EVENTS
        // =========================

        private void btnRefresh_Click(object sender, RoutedEventArgs e)
        {
            BuildRootTree();
            ApplySearch();
            lblStatus.Text = "Refreshed.";
        }

        private void tbSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplySearch();
        }

        private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var node = tvRoot?.SelectedItem as BrowserNode;
            if (node == null) return;
            if (!node.IsLeaf) return;
            if (node.ElementId == null || node.ElementId == ElementId.InvalidElementId) return;

            ActivateRequest.Set(node.ElementId);
            _activateEvent?.Raise();
        }

        private async void btnPublish_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // FIXED: Cancel previous operation và tạo token mới
                _publishCts?.Cancel();
                _publishCts = new CancellationTokenSource();

                btnPublish.IsEnabled = false;
                lblStatus.Text = "Publishing browser index...";

                // Nếu user vừa đổi server/projectId (textbox) thì restart poller đúng config
                StartOrRestartPoller(force: false);

                var server = (tbServer.Text ?? "").Trim().TrimEnd('/');
                var projectId = (tbProjectId.Text ?? "").Trim();

                if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(projectId))
                {
                    MessageBox.Show("Server/ProjectId không hợp lệ.", "Publish", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var index = BuildBrowserIndex(projectId);
                var url = $"{server}/api/projects/{Uri.EscapeDataString(projectId)}/browser-index";
                var json = JsonConvert.SerializeObject(index);

                // --- SỬA ĐOẠN NÀY ĐỂ THÊM API KEY ---

                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                // Thêm API Key vào Header (Khớp với giá trị mặc định trong app.py)
                // Trong thực tế, bạn nên lưu key này vào Settings thay vì hardcode
                request.Headers.Add("X-API-Key", "CHANGE-ME-IN-PRODUCTION");

                // Gửi request bằng SendAsync thay vì PostAsync
                var res = await SharedHttpClient.SendAsync(request, _publishCts.Token);

                // ------------------------------------

                var body = await res.Content.ReadAsStringAsync();

                if (!res.IsSuccessStatusCode)
                {
                    MessageBox.Show($"Publish failed ({(int)res.StatusCode}): {body}", "Publish", MessageBoxButton.OK, MessageBoxImage.Error);
                    lblStatus.Text = "Publish failed.";
                    return;
                }

                lblStatus.Text = "Published successfully.";
            }
            catch (OperationCanceledException)
            {
                lblStatus.Text = "Publish cancelled.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Publish error: {ex.Message}", "Publish", MessageBoxButton.OK, MessageBoxImage.Error);
                lblStatus.Text = "Publish error.";
            }
            finally
            {
                btnPublish.IsEnabled = true;
            }
        }

        // =========================
        // POLLER (WEB -> REVIT)
        // =========================

        private void StartOrRestartPoller(bool force)
        {
            var server = (tbServer.Text ?? "").Trim().TrimEnd('/');
            var projectId = (tbProjectId.Text ?? "").Trim();
            var clientId = Environment.MachineName;

            RemoteSettings.ServerBaseUrl = server;
            RemoteSettings.ProjectId = projectId;

            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(projectId))
                return;

            if (_remoteHandler == null)
            {
                _remoteHandler = new RemoteCommandHandler();
                _remoteEvent = ExternalEvent.Create(_remoteHandler);
            }

            if (!force &&
                _poller != null &&
                string.Equals(server, _pollerServer, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(projectId, _pollerProjectId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(clientId, _pollerClientId, StringComparison.OrdinalIgnoreCase))
            {
                return; // không đổi config => khỏi restart
            }

            StopPoller();

            _pollerServer = server;
            _pollerProjectId = projectId;
            _pollerClientId = clientId;

            _poller = new RemoteCommandPoller(server, projectId, clientId, _remoteEvent);
        }

        private void StopPoller()
        {
            // FIXED: Thêm proper error handling
            try
            {
                _poller?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error disposing poller: {ex.Message}");
            }
            _poller = null;
        }

        // =========================
        // TREE BINDING / SEARCH
        // =========================

        private void BindTree()
        {
            tvRoot.ItemsSource = new[] { _root };
        }

        private void ApplySearch()
        {
            var q = (tbSearch.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(q))
            {
                BindTree();
                return;
            }

            var filtered = FilterTree(_root, q);
            tvRoot.ItemsSource = filtered != null ? new[] { filtered } : Array.Empty<BrowserNode>();
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

        // =========================
        // BUILD ROOT TREE
        // =========================

        private void BuildRootTree()
        {
            _root = new BrowserNode
            {
                Title = "Project Browser",
                Type = BrowserNodeType.Folder,
                ElementId = ElementId.InvalidElementId
            };

            _root.Children.Add(BuildViewsTree(_doc));
            _root.Children.Add(BuildSheetsTree(_doc));
            _root.Children.Add(BuildSchedulesTree(_doc));
            _root.Children.Add(BuildFamiliesTree(_doc));
            _root.Children.Add(BuildGroupsTree(_doc));
            _root.Children.Add(BuildRevitLinksTree(_doc));
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

            return BrowserTreeBuilder.BuildTree(doc, "Views", views, org, e => (e as View)?.Name ?? e.Name);
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

            return BrowserTreeBuilder.BuildTree(doc, "Sheets", sheets, org, e =>
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

            var root = BrowserTreeBuilder.BuildTree(doc, "Schedules/Quantities", schedules, org, e => (e as ViewSchedule)?.Name ?? e.Name);

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

                var subTree = BrowserTreeBuilder.BuildTree(doc, "Panel Schedules", panels, org, e => (e as View)?.Name ?? e.Name);
                schedulesRoot.Children.Add(subTree);
            }
            catch (Exception ex)
            {
                // FIXED: Log error thay vì catch rỗng
                System.Diagnostics.Debug.WriteLine($"Panel schedules not available: {ex.Message}");
            }
        }

        private BrowserNode BuildFamiliesTree(Document doc)
        {
            var root = new BrowserNode
            {
                Title = "Families",
                Type = BrowserNodeType.Folder,
                ElementId = ElementId.InvalidElementId
            };

            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .ToList();

            var byCategory = symbols
                .GroupBy(s => s.Category?.Name ?? "Unknown")
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

            foreach (var catGroup in byCategory)
            {
                var catNode = new BrowserNode
                {
                    Title = catGroup.Key,
                    Type = BrowserNodeType.Folder,
                    ElementId = ElementId.InvalidElementId
                };

                var byFamily = catGroup
                    .GroupBy(s => s.Family?.Name ?? "(No Family)")
                    .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

                foreach (var famGroup in byFamily)
                {
                    var famNode = new BrowserNode
                    {
                        Title = famGroup.Key,
                        Type = BrowserNodeType.Folder,
                        ElementId = ElementId.InvalidElementId
                    };

                    foreach (var sym in famGroup.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                    {
                        famNode.Children.Add(new BrowserNode
                        {
                            Title = sym.Name,
                            Type = BrowserNodeType.Item,
                            ElementId = sym.Id
                        });
                    }

                    if (famNode.Children.Count > 0)
                        catNode.Children.Add(famNode);
                }

                if (catNode.Children.Count > 0)
                    root.Children.Add(catNode);
            }

            return root;
        }

        private BrowserNode BuildGroupsTree(Document doc)
        {
            var root = new BrowserNode
            {
                Title = "Groups",
                Type = BrowserNodeType.Folder,
                ElementId = ElementId.InvalidElementId
            };

            var groupTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(GroupType))
                .Cast<GroupType>()
                .ToList();

            var byCat = groupTypes
                .GroupBy(gt => gt.Category?.Name ?? "Unknown")
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

            foreach (var catGroup in byCat)
            {
                var catNode = new BrowserNode
                {
                    Title = catGroup.Key,
                    Type = BrowserNodeType.Folder,
                    ElementId = ElementId.InvalidElementId
                };

                foreach (var gt in catGroup.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    catNode.Children.Add(new BrowserNode
                    {
                        Title = gt.Name,
                        Type = BrowserNodeType.Item,
                        ElementId = gt.Id
                    });
                }

                if (catNode.Children.Count > 0)
                    root.Children.Add(catNode);
            }

            return root;
        }

        private BrowserNode BuildRevitLinksTree(Document doc)
        {
            var root = new BrowserNode
            {
                Title = "Revit Links",
                Type = BrowserNodeType.Folder,
                ElementId = ElementId.InvalidElementId
            };

            var linkTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .ToList();

            var linkInstances = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            var instByType = linkInstances
                .GroupBy(i => i.GetTypeId())
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var lt in linkTypes.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var typeNode = new BrowserNode
                {
                    Title = lt.Name,
                    Type = BrowserNodeType.Folder,
                    ElementId = lt.Id
                };

                if (instByType.TryGetValue(lt.Id, out var insts))
                {
                    foreach (var inst in insts.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                    {
                        typeNode.Children.Add(new BrowserNode
                        {
                            Title = inst.Name,
                            Type = BrowserNodeType.Item,
                            ElementId = inst.Id
                        });
                    }
                }

                root.Children.Add(typeNode);
            }

            return root;
        }

        // =========================
        // BUILD BROWSER INDEX JSON
        // =========================

        private object BuildBrowserIndex(string projectId)
        {
            var nodes = new List<object>();

            foreach (var branch in _root.Children)
            {
                TraverseLeaf(branch, new List<string>(), nodes);
            }

            var modelTitle = "";
            try
            {
                modelTitle = _doc?.Title ?? "";
            }
            catch (Exception ex)
            {
                // FIXED: Log error thay vì catch rỗng
                System.Diagnostics.Debug.WriteLine($"Could not get document title: {ex.Message}");
            }

            return new Dictionary<string, object>
            {
                ["projectId"] = projectId,
                ["generatedAt"] = DateTimeOffset.Now.ToString("o"),
                ["revitModelTitle"] = modelTitle,
                ["nodes"] = nodes
            };
        }

        private void TraverseLeaf(BrowserNode node, List<string> path, List<object> output)
        {
            if (node == null) return;

            if (node.Type == BrowserNodeType.Folder)
            {
                path.Add(node.Title);

                foreach (var c in node.Children)
                    TraverseLeaf(c, path, output);

                path.RemoveAt(path.Count - 1);
                return;
            }

            var id = node.ElementId;
            if (id == null || id == ElementId.InvalidElementId) return;

            var elem = _doc.GetElement(id);
            if (elem == null) return;

            var kind = "item";
            if (elem is View v)
            {
                if (v is ViewSheet) kind = "sheet";
                else if (v is ViewSchedule) kind = "schedule";
                else kind = "view";
            }
            else if (elem is FamilySymbol) kind = "family_type";
            else if (elem is GroupType) kind = "group_type";
            else if (elem is RevitLinkInstance) kind = "revit_link_instance";
            else if (elem is RevitLinkType) kind = "revit_link_type";

            var uniqueId = "";
            try { uniqueId = elem.UniqueId; } catch { }

            var meta = new Dictionary<string, object>();
            if (elem is View vv)
            {
                try { meta["viewType"] = vv.ViewType.ToString(); } catch { }
                try { meta["discipline"] = vv.Discipline.ToString(); } catch { }
            }

            output.Add(new Dictionary<string, object>
            {
                ["nodeId"] = string.Join("|", path.Concat(new[] { node.Title })),
                ["kind"] = kind,
                ["title"] = node.Title,
                ["path"] = path.ToArray(),
                ["revit"] = new Dictionary<string, object>
                {
                    ["elementId"] = id.IntegerValue,
                    ["uniqueId"] = uniqueId
                },
                ["meta"] = meta
            });
        }
    }
}