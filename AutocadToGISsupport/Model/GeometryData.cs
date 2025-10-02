using System.Collections.Generic;

namespace AutocadToGISsupport.Models
{
    public class GeometryData
    {
        public string Type { get; set; } // Polygon, LineString
        public List<List<double>> Coordinates { get; set; }
    }
}
