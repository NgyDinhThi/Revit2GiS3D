using System.Collections.Generic;

namespace AutocadToGISsupport.Models
{
    public class FeatureData
    {
        public GeometryData Geometry { get; set; }
        public Dictionary<string, object> Properties { get; set; }
    }
}
