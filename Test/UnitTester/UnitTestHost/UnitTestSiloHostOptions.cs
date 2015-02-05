using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Samples.Testing
{
    [Serializable]
    public class SiloHandle
    {
        public Silo Silo { get; set; }
        public AppDomain AppDomain { get; set; }
        public UnitTestSiloOptions Options { get; set; }
        public string Name { get; set; }
        public Process Process { get; set; }
        public string MachineName { get; set; }
        public IPEndPoint Endpoint { get; set; }

        public override string ToString()
        {
            return String.Format("SiloHandle:{0}", Endpoint);
        }
    }

    public class UnitTestSiloOptions
    {
        public bool StartFreshOrleans { get; set; }
        public bool StartPrimary { get; set; }
        public bool StartSecondary { get; set; }
        public bool StartClient { get; set; }

        public FileInfo SiloConfigFile { get; set; }

        public bool PickNewDeploymentId { get; set; }
        public bool PropagateActivityId { get; set; }
        public int BasePort { get; set; }
        public string MachineName { get; set; }
        public int LargeMessageWarningThreshold { get; set; }
        public GlobalConfiguration.LivenessProviderType LivenessType { get; set; }

        public UnitTestSiloOptions()
        {
            // all defaults except:
            StartFreshOrleans = true;
            StartPrimary = true;
            StartSecondary = true;
            StartClient = true;
            PickNewDeploymentId = false;
            BasePort = -1; // use default from configuration file
            MachineName = ".";
            LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain;
        }

        public UnitTestSiloOptions Copy()
        {
            return new UnitTestSiloOptions
            {
                StartFreshOrleans = StartFreshOrleans,
                StartPrimary = StartPrimary,
                StartSecondary = StartSecondary,
                StartClient = StartClient,
                SiloConfigFile = SiloConfigFile,
                PickNewDeploymentId = PickNewDeploymentId,
                BasePort = BasePort,
                MachineName = MachineName,
                LargeMessageWarningThreshold = LargeMessageWarningThreshold,
                PropagateActivityId = PropagateActivityId,
                LivenessType = LivenessType
            };
        }
    }

    public class UnitTestClientOptions
    {
        public FileInfo ClientConfigFile { get; set; }
        public TimeSpan ResponseTimeout { get; set; }
        public bool ProxiedGateway { get; set; }
        public List<IPEndPoint> Gateways { get; set; }
        public int PreferedGatewayIndex { get; set; }
        public bool PropagateActivityId { get; set; }

        public UnitTestClientOptions()
        {
            // all defaults except:
            ResponseTimeout = TimeSpan.FromSeconds(10);
            PreferedGatewayIndex = -1;
        }

        public UnitTestClientOptions Copy()
        {
            return new UnitTestClientOptions
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
