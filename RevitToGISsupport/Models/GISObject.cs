using System;
using System.Collections.Generic;

namespace RevitToGISsupport.Models
{
    public class GISObject
    {
        public string type { get; set; } = "Feature";
        public Geometry geometry { get; set; } = new Geometry();
        public Dictionary<string, object> properties { get; set; } = new Dictionary<string, object>();

        public GISObject(List<List<List<double>>> coords, Dictionary<string, object> props)
        {
            geometry.type = "Polygon";
            geometry.coordinates = coords;
            properties = props;
        }
    }

}
