using System;
using System.Diagnostics;
using System.Net;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Class that represents a handle to a silo
    /// </summary>
    [Serializable]
    public class SiloHandle
    {
        /// <summary> Get or set the silo </summary>
        public Silo Silo { get; set; }

        /// <summary> Get or set the AppDomain used by the silo </summary>
        public AppDomain AppDomain { get; set; }

#if !NETSTANDARD
        // TODO: remove?
        /// <summary> Get or set the TestingSiloOptions used by the silo </summary>
        public TestingSiloOptions Options { get; set; }
#endif

        /// <summary> Get or set configuration of the silo </summary>
        public NodeConfiguration NodeConfiguration { get; set; }

        /// <summary> Get or set the name of the silo </summary>
        public string Name { get; set; }

        /// <summary> Get or set process used by the silo </summary>
        public Process Process { get; set; }

        /// <summary> Get or set the machine name of the silo </summary>
        public string MachineName { get; set; }

        /// <summary> Get or set the endpoint of the silo </summary>
        public IPEndPoint Endpoint { get; set; }

        /// <summary> Get or set the gateway port of the silo </summary>
        public int? GatewayPort { get; set; }

        /// <summary> A string that represents the current SiloHandle </summary>
        public override string ToString()
        {
            return String.Format("(SiloHandle endpoint={0} gatewayport={1})", Endpoint, GatewayPort);
        }
    }
}
