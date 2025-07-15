using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RevitToGISsupport.Models
{
    public class Geometry
    {
        public string type { get; set; } = "Polygon";

        public double[][][] coordinates { get; set; }
    }
}
