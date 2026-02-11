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
            Directory.CreateDirectory(folder);

            // Dùng timestamp và tên view để tạo filename dự đoán được
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var viewName = SanitizeFileName(view.Name);
            var fileName = $"{viewName}_{timestamp}";
            var basePath = Path.Combine(folder, fileName);

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

            // Revit có thể thêm số vào cuối filename
            var expectedPattern = $"{fileName}*.png";
            var exportedFile = Directory.GetFiles(folder, expectedPattern)
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();

            return exportedFile;
        }

        /// <summary>
        /// Loại bỏ ký tự không hợp lệ khỏi tên file
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "view";

            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));

            // Giới hạn độ dài và loại bỏ dấu chấm cuối
            sanitized = sanitized.TrimEnd('.');
            return sanitized.Substring(0, Math.Min(50, sanitized.Length));
        }
    }
}