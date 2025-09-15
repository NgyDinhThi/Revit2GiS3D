using System;
using System.Collections.Generic;

namespace RevitToGISsupport.Models
{
    public class GISObject
    {
        public object geometry;
        public Dictionary<string, object> properties;

        public GISObject(List<List<List<double>>> polygon, Dictionary<string, object> props)
        {
            this.geometry = polygon;
            this.properties = props;
        }

        public GISObject(Dictionary<string, object> meshGeometry, Dictionary<string, object> props)
        {
            this.geometry = meshGeometry;
            this.properties = props;
        }
    }


}
