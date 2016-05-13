using System;
using System.Diagnostics;
using System.Net;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.TestingHost
{
    [Serializable]
    public class SiloHandle
    {
        public Silo Silo { get; set; }
        public AppDomain AppDomain { get; set; }
        // TODO: remove?
        public TestingSiloOptions Options { get; set; }
        public NodeConfiguration NodeConfiguration { get; set; }
        public string Name { get; set; }
        public Process Process { get; set; }
        public string MachineName { get; set; }
        public IPEndPoint Endpoint { get; set; }

        public override string ToString()
        {
            return String.Format("SiloHandle:{0}", Endpoint);
        }
    }
}
