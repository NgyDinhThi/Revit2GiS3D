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

            var prefix = "snap_" + Guid.NewGuid().ToString("N");
            var basePath = Path.Combine(folder, prefix);

            var before = Directory.GetFiles(folder, "*.png").ToHashSet(StringComparer.OrdinalIgnoreCase);

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

            var after = Directory.GetFiles(folder, "*.png");
            var created = after.Where(f => !before.Contains(f)).OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            if (created != null) return created;

            return after.OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        }
    }
}
