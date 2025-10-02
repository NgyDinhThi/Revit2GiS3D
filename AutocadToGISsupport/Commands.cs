using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using System;
using System.IO;

namespace AutocadToGISsupport
{
    public class Commands
    {
        // Lệnh: Eport (giữ theo cũ)
        [CommandMethod("Eport")]
        public void ExportActiveDrawingToGlb()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            try
            {
                string outFolder = @"C:\Temp\AutoCADtoGLB";
                Directory.CreateDirectory(outFolder);

                // Lấy dữ liệu trực tiếp từ bản vẽ đang mở
                var stream = DxfReader.CollectDataFromActiveDoc();

                if (stream == null || stream.Features == null || stream.Features.Count == 0)
                {
                    doc.Editor.WriteMessage("\n❌ Không tìm thấy đối tượng để export trong bản vẽ đang mở.\n");
                    return;
                }

                string glbPath = Path.Combine(outFolder, "active_drawing_model.glb");

                // Xuất GLB (hoặc đổi thành JsonExporter nếu muốn JSON)
                GLBExporter.ExportToGLB(stream, glbPath, true, 10000f);

                doc.Editor.WriteMessage($"\n✅ Đã export GLB từ bản vẽ đang mở: {glbPath}\nObjects: {stream.Features.Count}\n");
            }
            catch (System.Exception ex)
            {
                string inner = ex.InnerException != null ? $"\nInner: {ex.InnerException.Message}" : "";
                doc.Editor.WriteMessage($"\n❌ Lỗi export: {ex.Message}{inner}\n");
            }
        }
    }
}
