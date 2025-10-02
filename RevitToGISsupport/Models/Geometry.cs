using System;
using System.Collections.Generic;

namespace RevitToGISsupport.Models
{
    public class Geometry
    {
        public string type { get; set; }
        public object coordinates { get; set; } // can be List or nested lists
    }
}
