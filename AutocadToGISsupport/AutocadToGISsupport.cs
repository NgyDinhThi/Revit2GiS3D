using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using AutocadToGISsupport.Models;

namespace AutocadToGISsupport
{
    public static class AutocadToGISsupport
    {
        /// <summary>
        /// Gom dữ liệu từ AutoCAD (bản vẽ đang mở) thành StreamData
        /// </summary>
        public static StreamData CollectData()
        {
            var stream = new StreamData();

            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId entId in btr)
                {
                    Entity ent = tr.GetObject(entId, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    var props = new Dictionary<string, object>
                    {
                        { "Handle", ent.Handle.ToString() },
                        { "Layer", ent.Layer },
                        { "Type", ent.GetType().Name }
                    };

                    if (ent is Line line)
                    {
                        var coords = new List<List<double>>
                        {
                            new List<double> { line.StartPoint.X, line.StartPoint.Y, line.StartPoint.Z },
                            new List<double> { line.EndPoint.X, line.EndPoint.Y, line.EndPoint.Z }
                        };

                        var geom = new GeometryData
                        {
                            Type = "LineString",
                            Coordinates = coords
                        };

                        stream.Features.Add(new FeatureData
                        {
                            Geometry = geom,
                            Properties = props
                        });
                    }
                    else if (ent is Polyline pline)
                    {
                        var coords = new List<List<double>>();
                        for (int i = 0; i < pline.NumberOfVertices; i++)
                        {
                            Point2d pt = pline.GetPoint2dAt(i);
                            coords.Add(new List<double> { pt.X, pt.Y, 0 });
                        }

                        if (pline.Closed) coords.Add(coords[0]);

                        var geom = new GeometryData
                        {
                            Type = "Polygon",
                            Coordinates = coords
                        };

                        stream.Features.Add(new FeatureData
                        {
                            Geometry = geom,
                            Properties = props
                        });
                    }
                }

                tr.Commit();
            }

            Debug.WriteLine($"✅ Đã gom {stream.Features.Count} đối tượng từ AutoCAD");
            return stream;
        }

        /// <summary>
        /// Xuất dữ liệu AutoCAD thành file GLB
        /// </summary>
        public static void ExportGlbOnly(StreamData stream, string folderPath)
        {
            System.IO.Directory.CreateDirectory(folderPath);

            string glbPath = System.IO.Path.Combine(folderPath, "autocad_model.glb");
            GLBExporter.ExportToGLB(stream, glbPath);
        }
    }
}
