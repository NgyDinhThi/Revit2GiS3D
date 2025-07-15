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
                        if (wall.Location is LocationCurve location)
                        {
                            var start = location.Curve.GetEndPoint(0);
                            var end = location.Curve.GetEndPoint(1);

                            // Lấy chiều cao tường
                            double height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 10;
                            height = UnitUtils.ConvertFromInternalUnits(height, UnitTypeId.Meters);

                            // Chuyển X/Y sang Lon/Lat
                            double x1 = originLon + (start.X / metersPerDegLon);
                            double y1 = originLat + (start.Y / metersPerDegLat);
                            double x2 = originLon + (end.X / metersPerDegLon);
                            double y2 = originLat + (end.Y / metersPerDegLat);

                            // Offset nhỏ để tạo hình chữ nhật
                            double offset = 0.00005;

                            var gisObj = new GISObject
                            (
                                x1, y1,
                                x2, y2,
                                x2 + offset, y2 + offset,
                                x1 + offset, y1 + offset,
                                height
                            );

                            stream.objects.Add(gisObj);
                            count++;
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
