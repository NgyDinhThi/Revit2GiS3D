using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitToGISsupport.Models;
using RevitToGISsupport.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace RevitToGISsupport
{
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class ExportToGIS : IExternalCommand
    {
        double originLon = 105.85;
        double originLat = 21.03;
        double metersPerDegLon = 111320.0;
        double metersPerDegLat = 110540.0;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                OpenUI.CmdData = commandData;
                UIDocument uidoc = commandData.Application.ActiveUIDocument;
                Document doc = uidoc.Document;

                var stream = new GISStream
                {
                    streamId = Guid.NewGuid().ToString(),
                    objects = new List<GISObject>()
                };

                var categories = new BuiltInCategory[]
                {
                    BuiltInCategory.OST_Walls,
                    BuiltInCategory.OST_Floors,
                    BuiltInCategory.OST_Roofs,
                    BuiltInCategory.OST_StructuralFraming,
                    BuiltInCategory.OST_StructuralColumns,
                    BuiltInCategory.OST_StructuralFoundation
                };

                var collector = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .WherePasses(new ElementMulticategoryFilter(categories));

                int count = 0;

                foreach (Element element in collector)
                {
                    var opt = new Options
                    {
                        ComputeReferences = true,
                        IncludeNonVisibleObjects = false
                    };

                    GeometryElement geomElement = element.get_Geometry(opt);
                    if (geomElement == null) continue;

                    var props = new Dictionary<string, object>();
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

                    foreach (GeometryObject geomObj in geomElement)
                    {
                        if (geomObj is Solid solid && solid.Faces.Size > 0)
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
                                            XYZ pt = curve.GetEndPoint(0);
                                            double lon = originLon + (pt.X / metersPerDegLon);
                                            double lat = originLat + (pt.Y / metersPerDegLat);
                                            double ele = pt.Z;

                                            coords.Add(new List<double> { lon, lat, ele });
                                        }

                                        // Đóng polygon nếu chưa khép kín
                                        int last = coords.Count - 1;
                                        if (Math.Abs(coords[0][0] - coords[last][0]) > 1e-6 || Math.Abs(coords[0][1] - coords[last][1]) > 1e-6)

                                        {
                                            coords.Add(coords[0]);
                                        }

                                        var geometry = new Dictionary<string, object>
                    {
                        { "type", "Polygon" },
                        { "coordinates", new List<object> { coords } }
                    };

                                        var gisObj = new GISObject(geometry, props);
                                        stream.objects.Add(gisObj);
                                        count++;
                                    }
                                }
                                catch { continue; } // tránh lỗi nếu mặt không có loop hợp lệ
                            }
                        }
                    }
                }

                TaskDialog.Show("Export result", $"Tổng số đối tượng: {count}");

                var uploader = new GISUploader();
                bool success = Task.Run(() => uploader.Send(stream)).Result;

                string user = Environment.UserName;
                string status = success ? "Thành công" : "Thất bại";
                OpenUI.SaveSendHistory(user, status);

                if (!success)
                {
                    MessageBox.Show("Gửi dữ liệu thất bại hoặc mất kết nối.", "Thất bại", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                OpenUI.ShowMainUI();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi khi gửi dữ liệu: " + ex.Message, "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                return Result.Failed;
            }
        }
    }

    public class XYZComparer : IEqualityComparer<XYZ>
    {
        public bool Equals(XYZ a, XYZ b)
        {
            return a.IsAlmostEqualTo(b);
        }

        public int GetHashCode(XYZ obj)
        {
            return obj.GetHashCode();
        }
    }
}
