using System;
using System.Diagnostics;
using System.Net;
using Orleans.Runtime;

namespace Orleans.TestingHost
{
    [Serializable]
    public class SiloHandle
    {
        public Silo Silo { get; set; }
        public AppDomain AppDomain { get; set; }
        public TestingSiloOptions Options { get; set; }
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
