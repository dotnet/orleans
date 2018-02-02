using System;
using System.Collections.Generic;

namespace Orleans.Hosting
{
    public class GrainServiceOptions
    {
        public List<KeyValuePair<string, short>> GrainServices { get; set; } = new List<KeyValuePair<string, short>>();
    }
}
