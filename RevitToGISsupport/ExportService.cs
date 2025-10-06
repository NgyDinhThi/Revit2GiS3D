using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitToGISsupport.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace RevitToGISsupport.Services
{
    public static class ExportService
    {
        public static GISStream CollectData(Document doc, IProgress<int> progress = null)
        {
            var stream = new GISStream
            {
                streamId = Guid.NewGuid().ToString(),
                objects = new List<GISObject>()
            };

            // Build list of elements first so we know total count
            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            var elements = new List<Element>();
            foreach (Element e in collector)
                elements.Add(e);

            int total = Math.Max(1, elements.Count); // tránh chia cho 0
            int processed = 0;
            int reportedEvery = Math.Max(1, total / 200); // report ~200 lần max -> điều chỉnh nhẹ

            int count = 0;
            foreach (Element element in elements)
            {
                try
                {
                    var props = ExtractProperties(element, doc);

                    var opt = new Options
                    {
                        ComputeReferences = true,
                        IncludeNonVisibleObjects = true,
                        DetailLevel = ViewDetailLevel.Fine
                    };

                    GeometryElement geomElement = element.get_Geometry(opt);

                    if (geomElement != null)
                    {
                        // gọi ProcessGeometry với transform = null (nếu signature khác, điều chỉnh lại)
                        ProcessGeometry(geomElement, null, props, stream, ref count);
                    }
                    else if (element.Location is LocationPoint lp)
                    {
                        var geometry = new Dictionary<string, object>
                {
                    { "type", "Point" },
                    { "coordinates", new List<double> {
                        UnitUtils.ConvertFromInternalUnits(lp.Point.X, UnitTypeId.Meters),
                        UnitUtils.ConvertFromInternalUnits(lp.Point.Y, UnitTypeId.Meters),
                        UnitUtils.ConvertFromInternalUnits(lp.Point.Z, UnitTypeId.Meters)
                    }}
                };
                        stream.objects.Add(new GISObject(geometry, props));
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    // không throw tiếp, chỉ log để avoid crash Revit thread
                    System.Diagnostics.Debug.WriteLine($"CollectData: element error: {ex.Message}");
                }

                // progress
                processed++;
                if (progress != null && (processed % reportedEvery == 0 || processed == total))
                {
                    int pct = (int)((processed / (double)total) * 100.0);
                    if (pct < 0) pct = 0;
                    if (pct > 100) pct = 100;
                    progress.Report(pct);
                }
            }

            // đảm bảo báo 100% nếu có progress reporter
            progress?.Report(100);

            System.Diagnostics.Debug.WriteLine($"Đã gom {count} đối tượng từ Revit");
            return stream;
        }

        public static void ExportJsonAndGlb(GISStream stream, string folderPath)
        {
            Directory.CreateDirectory(folderPath);

            // JSON
            string jsonPath = Path.Combine(folderPath, "revit_model.json");
            File.WriteAllText(jsonPath, JsonConvert.SerializeObject(stream.ToGeoJson(), Formatting.Indented));

            // GLB
            string glbPath = Path.Combine(folderPath, "revit_model.glb");
            GLBExporter.ExportToGLB(stream, glbPath);
        }

        private static Dictionary<string, object> ExtractProperties(Element element, Document doc)
        {
            var props = new Dictionary<string, object>();
            props["Category"] = element.Category?.Name ?? "Unknown";
            props["ElementId"] = element.Id.ToString();
            props["Name"] = element.Name;

            foreach (Parameter param in element.Parameters)
            {
                if (!param.HasValue || param.Definition == null) continue;

                string name = param.Definition.Name;
                string val = "";

                switch (param.StorageType)
                {
                    case StorageType.Double:
                        val = UnitUtils.ConvertFromInternalUnits(param.AsDouble(), UnitTypeId.Meters).ToString("F2");
                        break;
                    case StorageType.Integer:
                        val = param.AsInteger().ToString();
                        break;
                    case StorageType.String:
                        val = param.AsString();
                        break;
                    case StorageType.ElementId:
                        var refElem = doc.GetElement(param.AsElementId());
                        val = refElem != null ? refElem.Name : param.AsElementId().Value.ToString();
                        break;
                }

                if (!string.IsNullOrEmpty(val))
                    props[name] = val;
            }

            return props;
        }

        // ProcessGeometry: now accepts a Transform parameter and passes it down
        private static void ProcessGeometry(GeometryElement geomElement, Transform transform, Dictionary<string, object> props, GISStream stream, ref int count)
        {
            foreach (GeometryObject geomObj in geomElement)
            {
                if (geomObj is Solid solid && solid.Faces.Size > 0)
                {
                    ProcessSolid(solid, transform, props, stream, ref count);
                }
                else if (geomObj is GeometryInstance instance)
                {
                    GeometryElement instGeom = instance.GetInstanceGeometry();
                    if (instGeom != null)
                    {
                        // truyền transform của instance xuống (kết hợp nếu cần)
                        Transform instTrans = instance.Transform;
                        // combine transforms if parent transform is not identity
                        Transform combined = transform != null ? transform.Multiply(instTrans) : instTrans;
                        ProcessGeometry(instGeom, combined, props, stream, ref count);
                    }
                }
                else if (geomObj is Mesh mesh)
                {
                    ProcessMesh(mesh, transform, props, stream, ref count);
                }
            }
        }


        private static void ProcessSolid(Solid solid, Transform transform, Dictionary<string, object> props, GISStream stream, ref int count)
        {
            foreach (Face face in solid.Faces)
            {
                try
                {
                    var edgeLoops = face.GetEdgesAsCurveLoops();
                    foreach (var loop in edgeLoops)
                    {
                        List<List<double>> coords = new List<List<double>>();
                        foreach (Curve curve in loop)
                        {
                            foreach (XYZ pt in curve.Tessellate())
                            {
                                XYZ p = (transform != null) ? transform.OfPoint(pt) : pt; // apply transform
                                coords.Add(new List<double> {
                                    UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Meters),
                                    UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Meters),
                                    UnitUtils.ConvertFromInternalUnits(p.Z, UnitTypeId.Meters)
                                });
                            }
                        }

                        // đảm bảo closed ring
                        if (coords.Count > 1)
                        {
                            int last = coords.Count - 1;
                            if (Math.Abs(coords[0][0] - coords[last][0]) > 1e-6 ||
                                Math.Abs(coords[0][1] - coords[last][1]) > 1e-6)
                            {
                                coords.Add(new List<double>(coords[0]));
                            }
                        }

                        // NOTE: coords là 1 ring; có thể thêm logic để xử lý nhiều ring (holes)
                        var geometry = new Dictionary<string, object>
                        {
                            { "type", "Polygon" },
                            { "coordinates", new List<object> { coords } } // outer ring only
                        };
                        stream.objects.Add(new GISObject(geometry, props));
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"❌ Face export error: {ex.Message}");
                }
            }
        }

        private static void ProcessMesh(Mesh mesh, Transform transform, Dictionary<string, object> props, GISStream stream, ref int count)
        {
            // xuất từng triangle của mesh
            int numTri = (int)mesh.NumTriangles; // cast safe (mesh sizes won't exceed int here)
            for (int t = 0; t < numTri; t++)
            {
                MeshTriangle tri = mesh.get_Triangle(t);
                var coords = new List<List<double>>();

                // MeshTriangle.get_Index(...) trả uint — cast sang int
                int i0 = (int)tri.get_Index(0);
                int i1 = (int)tri.get_Index(1);
                int i2 = (int)tri.get_Index(2);

                foreach (var idx in new[] { i0, i1, i2 })
                {
                    XYZ p = mesh.Vertices[idx];
                    if (transform != null) p = transform.OfPoint(p);
                    coords.Add(new List<double> {
                UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Meters),
                UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Meters),
                UnitUtils.ConvertFromInternalUnits(p.Z, UnitTypeId.Meters)
            });
                }

                // triangulated triangle -> output as polygon with 3 vertices
                var geometry = new Dictionary<string, object>
        {
            { "type", "Polygon" },
            { "coordinates", new List<object> { coords } }
        };
                stream.objects.Add(new GISObject(geometry, props));
                count++;
            }
        }

    }
}
