using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Client.Hosting
{
    public class OrleansClientStore
    {
        public IClusterClient Client { get; set; }
    }
}
