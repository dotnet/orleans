using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Orleans.Runtime.Configuration;

namespace Orleans.TestingHost
{
    public class TestingClientOptions
    {
        public const string DEFAULT_CLIENT_CONFIG_FILE = "ClientConfigurationForTesting.xml";

        public FileInfo ClientConfigFile { get; set; }
        public TimeSpan ResponseTimeout { get; set; }
        public bool ProxiedGateway { get; set; }
        public List<IPEndPoint> Gateways { get; set; }
        public int PreferedGatewayIndex { get; set; }
        public bool PropagateActivityId { get; set; }
        public Action<ClientConfiguration> AdjustConfig { get; set; }

        public TestingClientOptions()
        {
            // all defaults except:
            ResponseTimeout = TimeSpan.FromMilliseconds(-1);
            PreferedGatewayIndex = -1;
            ClientConfigFile = new FileInfo(DEFAULT_CLIENT_CONFIG_FILE);
        }

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
