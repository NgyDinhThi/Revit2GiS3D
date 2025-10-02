using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using System;
using System.Collections.Generic;
using System.IO;
using AutocadToGISsupport.Models;

namespace AutocadToGISsupport
{
    public static class DxfReader
    {
        /// <summary>
        /// Mở file DXF (path) bằng Database.DxfIn, gom dữ liệu và trả về StreamData.
        /// </summary>
        public static StreamData CollectDataFromFile(string dxfPath)
        {
            if (string.IsNullOrWhiteSpace(dxfPath)) throw new ArgumentNullException(nameof(dxfPath));
            if (!File.Exists(dxfPath)) throw new FileNotFoundException(dxfPath);

            var stream = new StreamData();

            // Tạo Database tạm và import DXF
            using (var db = new Database(false, true))
            {
                try
                {
                    db.DxfIn(dxfPath, dxfPath + ".log");
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException($"Không thể đọc DXF: {ex.Message}", ex);
                }

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    CollectEntitiesFromBlockTableRecord(tr, btr, stream);

                    tr.Commit();
                }
            }

            return stream;
        }

        /// <summary>
        /// Gom dữ liệu từ bản vẽ đang mở (Document) — giữ cho backward compatibility.
        /// </summary>
        public static StreamData CollectDataFromActiveDoc()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return new StreamData();
            return CollectFromDatabase(doc.Database);
        }

        // internal helper sử dụng chung cho cả 2 phương pháp
        static StreamData CollectFromDatabase(Database db)
        {
            var stream = new StreamData();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                CollectEntitiesFromBlockTableRecord(tr, btr, stream);

                tr.Commit();
            }

            return stream;
        }

        // trích entity từ một BlockTableRecord vào StreamData
        static void CollectEntitiesFromBlockTableRecord(Transaction tr, BlockTableRecord btr, StreamData stream)
        {
            foreach (ObjectId entId in btr)
            {
                var ent = tr.GetObject(entId, OpenMode.ForRead) as Entity;
                if (ent == null) continue;

                var props = new Dictionary<string, object>
                {
                    { "Handle", ent.Handle.ToString() },
                    { "Layer", ent.Layer },
                    { "Type", ent.GetType().Name }
                };

                // LINE
                if (ent is Line line)
                {
                    var coords = new List<List<double>>
                    {
                        new List<double>{ line.StartPoint.X, line.StartPoint.Y, line.StartPoint.Z },
                        new List<double>{ line.EndPoint.X, line.EndPoint.Y, line.EndPoint.Z }
                    };
                    stream.Features.Add(new FeatureData { Geometry = new GeometryData { Type = "LineString", Coordinates = coords }, Properties = props });
                    continue;
                }

                // POLYLINE 2D
                if (ent is Autodesk.AutoCAD.DatabaseServices.Polyline p2d)
                {
                    var ring = new List<List<double>>();
                    for (int i = 0; i < p2d.NumberOfVertices; i++)
                    {
                        var pt = p2d.GetPoint2dAt(i);
                        ring.Add(new List<double> { pt.X, pt.Y, 0.0 });
                    }

                    bool closed = p2d.Closed;
                    if (closed && ring.Count > 0 && !IsSame2DPoint(ring[0], ring[ring.Count - 1]))
                        ring.Add(new List<double>(ring[0]));

                    if (closed)
                        stream.Features.Add(new FeatureData { Geometry = new GeometryData { Type = "Polygon", Coordinates = ring }, Properties = props });
                    else
                        stream.Features.Add(new FeatureData { Geometry = new GeometryData { Type = "LineString", Coordinates = ring }, Properties = props });

                    continue;
                }

                // POLYLINE 3D
                if (ent is Polyline3d pl3d)
                {
                    var ring = new List<List<double>>();
                    foreach (ObjectId vId in pl3d)
                    {
                        var v = tr.GetObject(vId, OpenMode.ForRead) as PolylineVertex3d;
                        if (v == null) continue;
                        var p = v.Position;
                        ring.Add(new List<double> { p.X, p.Y, p.Z });
                    }

                    bool closed = pl3d.Closed;
                    if (closed && ring.Count > 0 && !IsSame2DPoint(ring[0], ring[ring.Count - 1]))
                        ring.Add(new List<double>(ring[0]));

                    if (closed)
                        stream.Features.Add(new FeatureData { Geometry = new GeometryData { Type = "Polygon", Coordinates = ring }, Properties = props });
                    else
                        stream.Features.Add(new FeatureData { Geometry = new GeometryData { Type = "LineString", Coordinates = ring }, Properties = props });

                    continue;
                }

                // CIRCLE
                if (ent is Circle circ)
                {
                    int segments = ClampInt((int)(circ.Radius * 6), 16, 256);
                    var ring = new List<List<double>>(segments + 1);
                    for (int i = 0; i < segments; i++)
                    {
                        double a = 2.0 * Math.PI * i / segments;
                        ring.Add(new List<double> { circ.Center.X + circ.Radius * Math.Cos(a), circ.Center.Y + circ.Radius * Math.Sin(a), circ.Center.Z });
                    }
                    ring.Add(new List<double>(ring[0]));
                    stream.Features.Add(new FeatureData { Geometry = new GeometryData { Type = "Polygon", Coordinates = ring }, Properties = props });
                    continue;
                }

                // ARC
                if (ent is Arc arc)
                {
                    int segments = 24;
                    double start = arc.StartAngle;
                    double end = arc.EndAngle;
                    double sweep = end - start;
                    if (sweep <= 0) sweep += 2.0 * Math.PI;
                    var pts = new List<List<double>>();
                    for (int i = 0; i <= segments; i++)
                    {
                        double t = start + sweep * i / segments;
                        pts.Add(new List<double> { arc.Center.X + arc.Radius * Math.Cos(t), arc.Center.Y + arc.Radius * Math.Sin(t), arc.Center.Z });
                    }
                    stream.Features.Add(new FeatureData { Geometry = new GeometryData { Type = "LineString", Coordinates = pts }, Properties = props });
                    continue;
                }

                // other entity types skipped for now
            }
        }

        // helper tương thích .NET Framework 4.8
        static int ClampInt(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        static bool IsSame2DPoint(List<double> a, List<double> b, double eps = 1e-6)
        {
            if (a == null || b == null || a.Count < 2 || b.Count < 2) return false;
            return Math.Abs(a[0] - b[0]) < eps && Math.Abs(a[1] - b[1]) < eps;
        }
    }
}
