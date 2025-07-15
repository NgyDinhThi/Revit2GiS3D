using System;
using System.Collections.Generic;

namespace RevitToGISsupport.Models
{
    public class GISObject
    {
        public string type { get; set; } = "Feature";
        public Geometry geometry { get; set; }
        public Dictionary<string, object> properties { get; set; }

        public GISObject() { }

        public GISObject(double x1, double y1, double x2, double y2, double x3, double y3, double x4, double y4, double height)
        {
            geometry = new Geometry
            {
                type = "Polygon",
                coordinates = new[]
                {
                new[]
                {
                    new[] { x1, y1 },
                    new[] { x2, y2 },
                    new[] { x3, y3 },
                    new[] { x4, y4 },
                    new[] { x1, y1 } // đóng vòng
                }
            }
            };

            properties = new Dictionary<string, object>
        {
            { "height", height }
        };
        }
    }
    
    }
