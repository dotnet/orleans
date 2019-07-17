using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Client.Hosting
{
    /// <summary>
    /// 
    /// </summary>
    public class ExampleClassController
    {
        private IClusterClient client;
        public ExampleClassController(IOrleansHostedClientAccessor accessor)
        {
            this.client = accessor.GetClient("MainClientOne");
            //this.client.GetGrain<> etc.
        }
    }
}
