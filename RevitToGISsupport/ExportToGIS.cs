using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitToGISsupport.Services;
using System;
using System.IO;
using System.Windows.Forms; // cần reference System.Windows.Forms
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace RevitToGISsupport
{
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class ExportToGIS : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                Document doc = commandData.Application.ActiveUIDocument.Document;

                // 1. Thu thập dữ liệu từ model
                var stream = ExportService.CollectData(doc);

                // 2. Lưu ExternalCommandData và stream để UI có thể dùng lại (nếu mở UI)
                OpenUI.CmdData = commandData;
                OpenUI.LastStream = stream;

                // 3. Hỏi user chọn thư mục lưu (FolderBrowserDialog)
                string folderPath = null;
                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "Chọn thư mục để lưu revit_model.json và revit_model.glb";
                    dlg.ShowNewFolderButton = true;
                    string defaultDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        "RevitExport_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                    dlg.SelectedPath = defaultDir;

                    var res = dlg.ShowDialog();
                    if (res == DialogResult.OK || res == DialogResult.Yes)
                    {
                        folderPath = dlg.SelectedPath;
                    }
                }

                if (string.IsNullOrWhiteSpace(folderPath))
                {
                    folderPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        "RevitExport_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                }

                Directory.CreateDirectory(folderPath);

                // 4. Xuất cả (JSON + GLB)
                ExportService.ExportJsonAndGlb(stream, folderPath);

                // 5. Thông báo thành công + show paths
                string msg = $"✅ Xuất thành công!\n\nJSON: {Path.Combine(folderPath, "revit_model.json")}\nGLB:  {Path.Combine(folderPath, "revit_model.glb")}";
                TaskDialog.Show("Export to GIS", msg);

                // 6. Mở UI (nếu muốn) — UI có thể dùng OpenUI.LastStream
                OpenUI.ShowMainUI();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Export to GIS - Error", "❌ Lỗi khi xuất: " + ex.Message + "\n\nXem Output/Debug để chi tiết.");
                System.Diagnostics.Debug.WriteLine(ex.ToString());
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
