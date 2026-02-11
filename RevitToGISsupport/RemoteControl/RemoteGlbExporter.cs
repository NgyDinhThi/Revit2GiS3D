using Autodesk.Revit.DB;
using RevitToGISsupport.Models;
using RevitToGISsupport.Services;
using System;
using System.Collections.Generic;
using System.IO;

namespace RevitToGISsupport.RemoteControl
{
    public static class RemoteGlbExporter
    {
        public static string ExportGlbForView(Document doc, View view, string folderPath)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (view == null) throw new ArgumentNullException(nameof(view));

            Directory.CreateDirectory(folderPath);

            var stream = CollectStream(doc, view);
            var glbPath = Path.Combine(folderPath, $"revit_view_{Guid.NewGuid():N}.glb");

            GLBExporter.ExportToGLB(stream, glbPath);

            if (!File.Exists(glbPath))
                throw new IOException("GLB export failed: output file not created.");

            return glbPath;
        }

        public static string ExportGlbForProject(Document doc, string folderPath)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            Directory.CreateDirectory(folderPath);

            var stream = CollectStream(doc, null);
            var glbPath = Path.Combine(folderPath, $"revit_project_{Guid.NewGuid():N}.glb");

            GLBExporter.ExportToGLB(stream, glbPath);

            if (!File.Exists(glbPath))
                throw new IOException("GLB export failed: output file not created.");

            return glbPath;
        }

        private static GISStream CollectStream(Document doc, View view)
        {
            var stream = new GISStream
            {
                streamId = Guid.NewGuid().ToString(),
                objects = new List<GISObject>()
            };

            FilteredElementCollector collector = (view != null)
                ? new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType()
                : new FilteredElementCollector(doc).WhereElementIsNotElementType();

            var opt = new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = false
            };

            // IMPORTANT:
            // Nếu set View => KHÔNG set DetailLevel nữa (lỗi bạn đang gặp).
            if (view != null)
            {
                opt.View = view;
            }
            else
            {
                opt.DetailLevel = ViewDetailLevel.Fine;
            }

            int count = 0;

            foreach (Element element in collector)
            {
                if (element == null) continue;

                GeometryElement geomElement = null;
                try { geomElement = element.get_Geometry(opt); } catch { }

                if (geomElement == null) continue;

                var props = new Dictionary<string, object>
                {
                    ["Category"] = element.Category?.Name ?? "Unknown",
                    ["ElementId"] = element.Id.IntegerValue.ToString()
                };

                ProcessGeometry(geomElement, props, stream, ref count);
            }

            return stream;
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
                    GeometryElement instGeom = null;
                    try { instGeom = instance.GetInstanceGeometry(); } catch { }

                    if (instGeom != null)
                        ProcessGeometry(instGeom, props, stream, ref count);
                }
            }
        }

        private static void ProcessSolid(Solid solid, Dictionary<string, object> props, GISStream stream, ref int count)
        {
            foreach (Face face in solid.Faces)
            {
                Mesh mesh = null;
                try { mesh = face.Triangulate(); } catch { }

                if (mesh == null || mesh.NumTriangles == 0) continue;

                for (int i = 0; i < mesh.NumTriangles; i++)
                {
                    var tri = mesh.get_Triangle(i);

                    var coords = new List<List<double>>
                    {
                        ToMeters(tri.get_Vertex(0)),
                        ToMeters(tri.get_Vertex(1)),
                        ToMeters(tri.get_Vertex(2)),
                        ToMeters(tri.get_Vertex(0))
                    };

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

        private static List<double> ToMeters(XYZ pt)
        {
            return new List<double>
            {
                UnitUtils.ConvertFromInternalUnits(pt.X, UnitTypeId.Meters),
                UnitUtils.ConvertFromInternalUnits(pt.Y, UnitTypeId.Meters),
                UnitUtils.ConvertFromInternalUnits(pt.Z, UnitTypeId.Meters)
            };
        }
    }
}
