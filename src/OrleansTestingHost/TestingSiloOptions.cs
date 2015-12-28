using System;
using System.IO;
using Orleans.Runtime.Configuration;

namespace Orleans.TestingHost
{
    public class TestingSiloOptions
    {
        public const string DEFAULT_SILO_CONFIG_FILE = "OrleansConfigurationForTesting.xml";

        public bool StartFreshOrleans { get; set; }
        public bool StartPrimary { get; set; }
        public bool StartSecondary { get; set; }
        public bool StartClient { get; set; }

        public FileInfo SiloConfigFile { get; set; }

        public bool PickNewDeploymentId { get; set; }
        public bool PropagateActivityId { get; set; }
        public int BasePort { get; set; }
        public int ProxyBasePort { get; set; }
        public string MachineName { get; set; }
        public int LargeMessageWarningThreshold { get; set; }
        public GlobalConfiguration.LivenessProviderType LivenessType { get; set; }
        public bool ParallelStart { get; set; }
        public GlobalConfiguration.ReminderServiceProviderType ReminderServiceType { get; set; }
        public string DataConnectionString { get; set; }
        public Action<ClusterConfiguration> AdjustConfig { get; set; }

        public TestingSiloOptions()
        {
            // all defaults except:
            StartFreshOrleans = true;
            StartPrimary = true;
            StartSecondary = true;
            StartClient = true;
            PickNewDeploymentId = true;
            BasePort = -1; // use default from configuration file
            ProxyBasePort = -1; 
            MachineName = ".";
            LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain;
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain;
            SiloConfigFile = new FileInfo(DEFAULT_SILO_CONFIG_FILE);
            ParallelStart = false;
        }

        public TestingSiloOptions Copy()
        {
            return new TestingSiloOptions
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
                LivenessType = LivenessType,
                ReminderServiceType = ReminderServiceType,
                DataConnectionString = DataConnectionString,
                ParallelStart = ParallelStart,
            };
        }
    }
}
