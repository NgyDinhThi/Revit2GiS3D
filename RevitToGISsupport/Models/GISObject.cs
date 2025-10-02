using System;
using System.Collections.Generic;

namespace RevitToGISsupport.Models
{
    public class GISObject
    {
        public object geometry { get; set; }
        public Dictionary<string, object> properties { get; set; }

        // Constructor when you already have a geometry dictionary (recommended)
        public GISObject(Dictionary<string, object> meshGeometry, Dictionary<string, object> props)
        {
            this.geometry = meshGeometry;
            this.properties = props ?? new Dictionary<string, object>();
        }

        // Convenience constructor for a single linear ring (List of [x,y,z])
        // Accepts List<List<double>> for one ring and wraps it as Polygon coordinates
        public GISObject(List<List<double>> ring, Dictionary<string, object> props)
        {
            var coordsList = new List<object> { ring }; // polygon with one ring
            var geom = new Dictionary<string, object>
            {
                { "type", "Polygon" },
                { "coordinates", coordsList }
            };
            this.geometry = geom;
            this.properties = props ?? new Dictionary<string, object>();
        }

        // Return a GeoJSON Feature object
        public object ToFeature()
        {
            return new
            {
                type = "Feature",
                geometry = this.geometry,
                properties = this.properties
            };
        }
    }
}
