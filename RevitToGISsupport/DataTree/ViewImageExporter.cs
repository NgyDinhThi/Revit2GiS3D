using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RevitToGISsupport.RemoteControl
{
    public static class ViewImageExporter
    {
        public static string ExportPng(Document doc, View view, string folder, int pixelSize)
        {
            if (view is ViewSchedule)
                throw new Exception("Không thể chụp ảnh cho Bảng thống kê (Schedule). Vui lòng chọn View hoặc Sheet.");

            Directory.CreateDirectory(folder);

            // Dọn sạch các file ảnh cũ bị kẹt lại
            try
            {
                foreach (var f in Directory.GetFiles(folder, "*.png"))
                {
                    File.Delete(f);
                }
            }
            catch { }

            var basePath = Path.Combine(folder, "temp_render");

            var opt = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = basePath,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = Math.Max(1000, pixelSize)
            };

            opt.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(opt);

            // Tóm chính xác file PNG vừa tạo ra
            var exportedFile = Directory.GetFiles(folder, "*.png")
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(exportedFile))
                throw new Exception("Revit không tạo được file ảnh.");

            return exportedFile;
        }
    }
}