using System;
using System.Collections.Generic;

namespace AutocadToGISsupport.Models
{
    public class StreamData
    {
        public string StreamId { get; set; } = Guid.NewGuid().ToString();
        public List<FeatureData> Features { get; set; } = new List<FeatureData>();
    }
}
