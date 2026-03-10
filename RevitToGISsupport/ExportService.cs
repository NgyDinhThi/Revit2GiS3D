using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitToGISsupport.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace RevitToGISsupport.Services
{
    public static class ExportService
    {
        public static GISStream CollectData(Document doc)
        {
            var stream = new GISStream
            {
                streamId = Guid.NewGuid().ToString(),
                objects = new List<GISObject>()
            };

            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType();

            int count = 0;

            foreach (Element element in collector)
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
                    ProcessGeometry(geomElement, props, stream, ref count);
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

            Debug.WriteLine($"✅ Đã gom {count} đối tượng từ Revit");
            return stream;
        }

        public static GISStream CollectData(Document doc, View view)
        {
            var stream = new GISStream
            {
                streamId = Guid.NewGuid().ToString(),
                objects = new List<GISObject>()
            };

            FilteredElementCollector collector =
                view != null
                ? new FilteredElementCollector(doc, view.Id)
                : new FilteredElementCollector(doc);

            collector = collector.WhereElementIsNotElementType();

            int count = 0;

            var opt = new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = true,
                DetailLevel = ViewDetailLevel.Fine
            };

            foreach (Element element in collector)
            {
                var props = ExtractProperties(element, doc);

                GeometryElement geomElement = element.get_Geometry(opt);
                if (geomElement != null)
                {
                    ProcessGeometry(geomElement, props, stream, ref count);
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

            Debug.WriteLine($"✅ Đã gom {count} đối tượng theo View");
            return stream;
        }

        // [ĐÃ SỬA]: Chỉ giữ lại hàm xuất JSON, bỏ qua GLB lỗi thời
        public static void ExportJson(GISStream stream, string folderPath)
        {
            Directory.CreateDirectory(folderPath);

            string jsonPath = Path.Combine(folderPath, "revit_model.json");
            File.WriteAllText(jsonPath, JsonConvert.SerializeObject(stream.ToGeoJson(), Formatting.Indented));
        }

        private static Dictionary<string, object> ExtractProperties(Element element, Document doc)
        {
            var props = new Dictionary<string, object>();
            props["Category"] = element.Category?.Name ?? "Unknown";
            props["ElementId"] = element.Id.ToString();

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

        private static void ProcessGeometry(GeometryElement geomElement, Dictionary<string, object> props,
                                            GISStream stream, ref int count)
        {
            foreach (GeometryObject geomObj in geomElement)
            {
                if (geomObj is Solid solid && solid.Faces.Size > 0)
                {
                    ProcessSolid(solid, props, stream, ref count);
                }
                else if (geomObj is GeometryInstance instance)
                {
                    GeometryElement instGeom = instance.GetInstanceGeometry();
                    if (instGeom != null) ProcessGeometry(instGeom, props, stream, ref count);
                }
            }
        }

        private static void ProcessSolid(Solid solid, Dictionary<string, object> props,
                                         GISStream stream, ref int count)
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
                                coords.Add(new List<double> {
                                    UnitUtils.ConvertFromInternalUnits(pt.X, UnitTypeId.Meters),
                                    UnitUtils.ConvertFromInternalUnits(pt.Y, UnitTypeId.Meters),
                                    UnitUtils.ConvertFromInternalUnits(pt.Z, UnitTypeId.Meters)
                                });
                            }
                        }

                        if (coords.Count > 1)
                        {
                            int last = coords.Count - 1;
                            if (Math.Abs(coords[0][0] - coords[last][0]) > 1e-6 ||
                                Math.Abs(coords[0][1] - coords[last][1]) > 1e-6)
                            {
                                coords.Add(coords[0]);
                            }
                        }

                        var geometry = new Dictionary<string, object>
                        {
                            { "type", "Polygon" },
                            { "coordinates", new List<object> { coords } }
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
    }
}