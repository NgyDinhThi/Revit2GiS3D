using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitToGISsupport.Models;
using RevitToGISsupport.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;

namespace RevitToGISsupport
{
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class ExportToGIS : IExternalCommand
    {
        // Tọa độ gốc
        double originLon = 105.85;
        double originLat = 21.03;

        // Quy đổi mét → độ
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

                var walls = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .OfCategory(BuiltInCategory.OST_Walls);

                int count = 0;

                foreach (var element in walls)
                {
                    if (element is Wall wall)
                    {
                        // Lấy chiều cao tường
                        double height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 10;
                        height = UnitUtils.ConvertFromInternalUnits(height, UnitTypeId.Meters);

                        // Tạo tùy chọn đọc hình học
                        Options opt = new Options
                        {
                            ComputeReferences = true,
                            IncludeNonVisibleObjects = false
                        };

                        GeometryElement geomElement = wall.get_Geometry(opt);

                        foreach (GeometryObject geomObj in geomElement)
                        {
                            if (geomObj is Solid solid && solid.Faces.Size > 0)
                            {
                                foreach (Face face in solid.Faces)
                                {
                                    Mesh mesh = face.Triangulate();
                                    List<List<double>> polygon = new List<List<double>>();

                                    foreach (XYZ pt in mesh.Vertices)
                                    {
                                        double lon = originLon + (pt.X / metersPerDegLon);
                                        double lat = originLat + (pt.Y / metersPerDegLat);
                                        polygon.Add(new List<double> { lon, lat });
                                    }

                                    // Đảm bảo polygon được đóng vòng
                                    if (polygon.Count > 0 &&
                                        (polygon[0][0] != polygon[polygon.Count - 1][0] || polygon[0][1] != polygon[polygon.Count - 1][1]))
                                    {
                                        polygon.Add(new List<double>(polygon[0]));
                                    }

                                    var geoPolygon = new List<List<List<double>>> { polygon };
                                    var props = new Dictionary<string, object> { { "height", height } };

                                    var gisObj = new GISObject(geoPolygon, props);
                                    stream.objects.Add(gisObj);
                                    count++;
                                    break; // Lấy 1 mặt là đủ
                                }
                            }
                        }
                    }
                }

                TaskDialog.Show("Export result", $"Tổng số tường: {count}");

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
}
