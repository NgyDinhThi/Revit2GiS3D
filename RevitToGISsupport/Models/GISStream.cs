using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace RevitToGISsupport.Models
{
    public class GISStream
    {
        public string streamId { get; set; }
        public List<GISObject> objects { get; set; } = new List<GISObject>();

        public object ToGeoJson()
        {
            return new
            {
                type = "FeatureCollection",
                features = this.objects
            };
        }
    }
}
