using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.IO;

namespace RevitToGISsupport.RemoteControl
{
    public sealed class RemoteCommandHandler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            var uidoc = app?.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return;

            while (RemoteCommandQueue.Items.TryDequeue(out var cmd))
            {
                try
                {
                    if (cmd == null) continue;
                    if (cmd.action != "activate_view") continue;
                    if (string.IsNullOrWhiteSpace(cmd.targetUniqueId)) continue;

                    var elem = doc.GetElement(cmd.targetUniqueId);
                    if (!(elem is View v)) { Log($"Not a View. uniqueId={cmd.targetUniqueId} type={(elem == null ? "null" : elem.GetType().FullName)}"); continue; }
                    if (v.IsTemplate) { Log($"IsTemplate. uniqueId={cmd.targetUniqueId} name={v.Name}"); continue; }

                    // request view change is safer for modeless/external event flows
                    try
                    {
                        uidoc.RequestViewChange(v);
                        Log($"RequestViewChange OK. view={v.Name}");
                    }
                    catch (Exception ex)
                    {
                        Log($"RequestViewChange FAIL -> fallback ActiveView. ex={ex.Message}");
                        uidoc.ActiveView = v;
                    }
                }
                catch (Exception ex)
                {
                    Log($"Handler exception: {ex}");
                }
            }
        }

        public string GetName() => "RemoteCommandHandler";

        private static void Log(string msg)
        {
            try
            {
                var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "revit_remote_open.log");
                File.AppendAllText(p, DateTime.Now.ToString("o") + " | " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
