using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Orleans.Runtime.Configuration;

namespace Orleans.TestingHost
{
    /// <summary> Client options to use in <see cref="TestingSiloHost"/> </summary>
    public class TestingClientOptions
    {
        /// <summary> Default path for the client config file </summary>
        public const string DEFAULT_CLIENT_CONFIG_FILE = "ClientConfigurationForTesting.xml";

        /// <summary> Get or set the client config file </summary>
        public FileInfo ClientConfigFile { get; set; }

        /// <summary> Get or set the response timeout </summary>
        public TimeSpan ResponseTimeout { get; set; }

        /// <summary> If set to true the property <see cref="PreferedGatewayIndex"/> will be used </summary>
        public bool ProxiedGateway { get; set; }

        /// <summary> Get or set the list of gateways to use </summary>
        public List<IPEndPoint> Gateways { get; set; }

        /// <summary> The index in <see cref="Gateways"/> list to use as the prefered gateway </summary>
        public int PreferedGatewayIndex { get; set; }

        /// <summary> If set to truem the activity id will be propagated </summary>
        public bool PropagateActivityId { get; set; }

        /// <summary> Delegate to apply transformation to the client configuration </summary>
        public Action<ClientConfiguration> AdjustConfig { get; set; }

        /// <summary> Construct a new TestingClientOptions using default value </summary>
        public TestingClientOptions()
        {
            // all defaults except:
            ResponseTimeout = TimeSpan.FromMilliseconds(-1);
            PreferedGatewayIndex = -1;
            ClientConfigFile = new FileInfo(DEFAULT_CLIENT_CONFIG_FILE);
        }

        /// <summary> Copy the current TestingClientOptions </summary>
        /// <returns>A copy of the target</returns>
        public TestingClientOptions Copy()
        {
            return new TestingClientOptions
            {
                ResponseTimeout = ResponseTimeout,
                ProxiedGateway = ProxiedGateway,
                Gateways = Gateways,
                PreferedGatewayIndex = PreferedGatewayIndex,
                PropagateActivityId = PropagateActivityId,
                ClientConfigFile = ClientConfigFile,
            };
        }
    }
}
