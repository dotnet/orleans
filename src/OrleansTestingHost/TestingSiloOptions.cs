using System;
using System.IO;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost.Utils;

namespace Orleans.TestingHost
{

    /// <summary> Silo options to use in <see cref="TestingSiloHost"/> </summary>
    public class TestingSiloOptions
    {
        /// <summary> Default path for the silo config file </summary>
        public const string DEFAULT_SILO_CONFIG_FILE = "OrleansConfigurationForTesting.xml";

        /// <summary> If set to true, will start a new orleans cluster </summary>
        public bool StartFreshOrleans { get; set; }

        /// <summary> If set to true, will start the primary cluster </summary>
        public bool StartPrimary { get; set; }

        /// <summary> If set to true, will start secondary clusters </summary>
        public bool StartSecondary { get; set; }

        /// <summary> If set to true, will start the client </summary>
        public bool StartClient { get; set; }

        /// <summary> Get or set the cluster config file </summary>
        public FileInfo SiloConfigFile { get; set; }

        /// <summary> If set to true, will generate a new deploymentId </summary>
        public bool PickNewDeploymentId { get; set; }

        /// <summary> If set to true, will propagate the activityId </summary>
        public bool PropagateActivityId { get; set; }

        /// <summary> Get or set the base port value for the silos </summary>
        public int BasePort { get; set; }

        /// <summary> Get or set the base port value for the silos gateway </summary>
        public int ProxyBasePort { get; set; }

        /// <summary> Get or set the machine name to display </summary>
        public string MachineName { get; set; }

        /// <summary> Get or set the warning thresold for large message </summary>
        public int LargeMessageWarningThreshold { get; set; }

        /// <summary> Get or set the liveness provider type to use in the cluster </summary>
        public GlobalConfiguration.LivenessProviderType LivenessType { get; set; }

        /// <summary> If set to true, will start in parallel the silos </summary>
        public bool ParallelStart { get; set; }

        /// <summary> Get or set the reminder provider type to use in the cluster </summary>
        public GlobalConfiguration.ReminderServiceProviderType ReminderServiceType { get; set; }

        /// <summary> Get or set the connection string to use in the cluster </summary>
        public string DataConnectionString { get; set; }

        /// <summary> Delegate to apply transformation to the cluster configuration </summary>
        public Action<ClusterConfiguration> AdjustConfig { get; set; }

        /// <summary> Construct a new TestingSiloOptions using default value </summary>
        public TestingSiloOptions()
        {
            // all defaults except:
            StartFreshOrleans = true;
            StartPrimary = true;
            StartSecondary = true;
            StartClient = true;
            PickNewDeploymentId = true;
            // BasePort = -1; // use default from configuration file
            BasePort = ThreadSafeRandom.Next(2000, 9999);
            ProxyBasePort = -1; 
            MachineName = ".";
            LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain;
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain;
            SiloConfigFile = new FileInfo(DEFAULT_SILO_CONFIG_FILE);
            ParallelStart = false;
        }

        /// <summary> Copy the current TestingSiloOptions </summary>
        /// <returns>A copy of the target</returns>
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
