using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Xml;
using Orleans.GrainDirectory;
using Orleans.Providers;
using Orleans.Storage;
using Orleans.Streams;
using Orleans.LogConsistency;

namespace Orleans.Runtime.Configuration
{
    // helper utility class to handle default vs. explicitly set config value.
    [Serializable]
    internal class ConfigValue<T>
    {
        public T Value;
        public bool IsDefaultValue;

        public ConfigValue(T val, bool isDefaultValue)
        {
            Value = val;
            IsDefaultValue = isDefaultValue;
        }
    }

    /// <summary>
    /// Data object holding Silo global configuration parameters.
    /// </summary>
    [Serializable]
    public class GlobalConfiguration : MessagingConfiguration
    {
        /// <summary>
        /// Liveness configuration that controls the type of the liveness protocol that silo use for membership.
        /// </summary>
        public enum LivenessProviderType
        {
            /// <summary>Default value to allow discrimination of override values.</summary>
            NotSpecified,
            /// <summary>Grain is used to store membership information. 
            /// This option is not reliable and thus should only be used in local development setting.</summary>
            MembershipTableGrain,
            /// <summary>AzureTable is used to store membership information. 
            /// This option can be used in production.</summary>
            AzureTable,
            /// <summary>SQL Server is used to store membership information. 
            /// This option can be used in production.</summary>
            SqlServer,
            /// <summary>Apache ZooKeeper is used to store membership information. 
            /// This option can be used in production.</summary>
            ZooKeeper,
            /// <summary>Use custom provider from third-party assembly</summary>
            Custom
        }

        /// <summary>
        /// Reminders configuration that controls the type of the protocol that silo use to implement Reminders.
        /// </summary>
        public enum ReminderServiceProviderType
        {
            /// <summary>Default value to allow discrimination of override values.</summary>
            NotSpecified,
            /// <summary>Grain is used to store reminders information. 
            /// This option is not reliable and thus should only be used in local development setting.</summary>
            ReminderTableGrain,
            /// <summary>AzureTable is used to store reminders information. 
            /// This option can be used in production.</summary>
            AzureTable,
            /// <summary>SQL Server is used to store reminders information. 
            /// This option can be used in production.</summary>
            SqlServer,
            /// <summary>Used for benchmarking; it simply delays for a specified delay during each operation.</summary>
            MockTable,
            /// <summary>Reminder Service is disabled.</summary>
            Disabled,
            /// <summary>Use custom Reminder Service from third-party assembly</summary>
            Custom
        }

        /// <summary>
        /// Configuration for Gossip Channels
        /// </summary>
        public enum GossipChannelType
        {
            /// <summary>Default value to allow discrimination of override values.</summary>
            NotSpecified,

            /// <summary>An Azure Table serving as a channel. </summary>
            AzureTable,
        }

        /// <summary>
        /// Gossip channel configuration.
        /// </summary>
        [Serializable]
        public class GossipChannelConfiguration
        {
            /// <summary>Gets or sets the gossip channel type.</summary>
            public GossipChannelType ChannelType { get; set; }

            /// <summary>Gets or sets the credential information used by the channel implementation.</summary>
            public string ConnectionString { get; set; }
        }
  
        /// <summary>
        /// Configuration type that controls the type of the grain directory caching algorithm that silo use.
        /// </summary>
        public enum DirectoryCachingStrategyType
        {
            /// <summary>Don't cache.</summary>
            None,
            /// <summary>Standard fixed-size LRU.</summary>
            LRU,
            /// <summary>Adaptive caching with fixed maximum size and refresh. This option should be used in production.</summary>
            Adaptive
        }

        public ApplicationConfiguration Application { get; private set; }

        /// <summary>
        /// SeedNodes are only used in local development setting with LivenessProviderType.MembershipTableGrain
        /// SeedNodes are never used in production.
        /// </summary>
        public IList<IPEndPoint> SeedNodes { get; private set; }

        /// <summary>
        /// The subnet on which the silos run. 
        /// This option should only be used when running on multi-homed cluster. It should not be used when running in Azure.
        /// </summary>
        public byte[] Subnet { get; set; }

        /// <summary>
        /// Determines if primary node is required to be configured as a seed node.
        /// True if LivenessType is set to MembershipTableGrain, false otherwise.
        /// </summary>
        public bool PrimaryNodeIsRequired
        {
            get { return LivenessType == LivenessProviderType.MembershipTableGrain; }
        }

        /// <summary>
        /// Global switch to disable silo liveness protocol (should be used only for testing).
        /// The LivenessEnabled attribute, if provided and set to "false", suppresses liveness enforcement.
        /// If a silo is suspected to be dead, but this attribute is set to "false", the suspicions will not propagated to the system and enforced,
        /// This parameter is intended for use only for testing and troubleshooting.
        /// In production, liveness should always be enabled.
        /// Default is true (eanabled)
        /// </summary>
        public bool LivenessEnabled { get; set; }
        /// <summary>
        /// The number of seconds to periodically probe other silos for their liveness or for the silo to send "I am alive" heartbeat  messages about itself.
        /// </summary>
        public TimeSpan ProbeTimeout { get; set; }
        /// <summary>
        /// The number of seconds to periodically fetch updates from the membership table.
        /// </summary>
        public TimeSpan TableRefreshTimeout { get; set; }
        /// <summary>
        /// Expiration time in seconds for death vote in the membership table.
        /// </summary>
        public TimeSpan DeathVoteExpirationTimeout { get; set; }
        /// <summary>
        /// The number of seconds to periodically write in the membership table that this silo is alive. Used ony for diagnostics.
        /// </summary>
        public TimeSpan IAmAliveTablePublishTimeout { get; set; }
        /// <summary>
        /// The number of seconds to attempt to join a cluster of silos before giving up.
        /// </summary>
        public TimeSpan MaxJoinAttemptTime { get; set; }
        /// <summary>
        /// The number of seconds to refresh the cluster grain interface map
        /// </summary>
        public TimeSpan TypeMapRefreshInterval { get; set; }
        internal ConfigValue<int> ExpectedClusterSizeConfigValue { get; set; }
        /// <summary>
        /// The expected size of a cluster. Need not be very accurate, can be an overestimate.
        /// </summary>
        public int ExpectedClusterSize { get { return ExpectedClusterSizeConfigValue.Value; } set { ExpectedClusterSizeConfigValue = new ConfigValue<int>(value, false); } }
        /// <summary>
        /// The number of missed "I am alive" heartbeat messages from a silo or number of un-replied probes that lead to suspecting this silo as dead.
        /// </summary>
        public int NumMissedProbesLimit { get; set; }
        /// <summary>
        /// The number of silos each silo probes for liveness.
        /// </summary>
        public int NumProbedSilos { get; set; }
        /// <summary>
        /// The number of non-expired votes that are needed to declare some silo as dead (should be at most NumMissedProbesLimit)
        /// </summary>
        public int NumVotesForDeathDeclaration { get; set; }
        /// <summary>
        /// The number of missed "I am alive" updates  in the table from a silo that causes warning to be logged. Does not impact the liveness protocol.
        /// </summary>
        public int NumMissedTableIAmAliveLimit { get; set; }
        /// <summary>
        /// Whether to use the gossip optimization to speed up spreading liveness information.
        /// </summary>
        public bool UseLivenessGossip { get; set; }
        /// <summary>
        /// Whether new silo that joins the cluster has to validate the initial connectivity with all other Active silos.
        /// </summary>
        public bool ValidateInitialConnectivity { get; set; }

        /// <summary>
        /// Service Id.
        /// </summary>
        public Guid ServiceId { get; set; }

        /// <summary>
        /// Deployment Id.
        /// </summary>
        public string DeploymentId { get; set; }

        #region MultiClusterNetwork

        /// <summary>
        /// Whether this cluster is configured to be part of a multicluster network
        /// </summary>
        public bool HasMultiClusterNetwork
        {
            get
            {
                return !(string.IsNullOrEmpty(ClusterId));
            }
        }

        /// <summary>
        /// Cluster id (one per deployment, unique across all the deployments/clusters)
        /// </summary>
        public string ClusterId { get; set; }

        /// <summary>
        ///A list of cluster ids, to be used if no multicluster configuration is found in gossip channels.
        /// </summary>
        public IReadOnlyList<string> DefaultMultiCluster { get; set; }

        /// <summary>
        /// The maximum number of silos per cluster should be designated to serve as gateways.
        /// </summary>
        public int MaxMultiClusterGateways { get; set; }

        /// <summary>
        /// The time between background gossips.
        /// </summary>
        public TimeSpan BackgroundGossipInterval { get; set; }

        /// <summary>
        /// Whether to use the global single instance protocol as the default
        /// multicluster registration strategy.
        /// </summary>
        public bool UseGlobalSingleInstanceByDefault { get; set; }
        
       /// <summary>
        /// The number of quick retries before going into DOUBTFUL state.
        /// </summary>
        public int GlobalSingleInstanceNumberRetries { get; set; }

        /// <summary>
        /// The time between the slow retries for DOUBTFUL activations.
        /// </summary>
        public TimeSpan GlobalSingleInstanceRetryInterval { get; set; }

 
        /// <summary>
        /// A list of connection strings for gossip channels.
        /// </summary>
        public IReadOnlyList<GossipChannelConfiguration> GossipChannels { get; set; }

        #endregion

        /// <summary>
        /// Connection string for the underlying data provider for liveness and reminders. eg. Azure Storage, ZooKeeper, SQL Server, ect.
        /// In order to override this value for reminders set <see cref="DataConnectionStringForReminders"/>
        /// </summary>
        public string DataConnectionString { get; set; }

        /// <summary>
        /// When using ADO, identifies the underlying data provider for liveness and reminders. This three-part naming syntax is also used 
        /// when creating a new factory and for identifying the provider in an application configuration file so that the provider name, 
        /// along with its associated connection string, can be retrieved at run time. https://msdn.microsoft.com/en-us/library/dd0w4a2z%28v=vs.110%29.aspx
        /// In order to override this value for reminders set <see cref="AdoInvariantForReminders"/> 
        /// </summary>
        public string AdoInvariant { get; set; }

        /// <summary>
        /// Set this property to override <see cref="DataConnectionString"/> for reminders.
        /// </summary>
        public string DataConnectionStringForReminders
        {
            get
            {
                return string.IsNullOrWhiteSpace(dataConnectionStringForReminders) ? DataConnectionString : dataConnectionStringForReminders;
            }
            set { dataConnectionStringForReminders = value; }
        }

        /// <summary>
        /// Set this property to override <see cref="AdoInvariant"/> for reminders.
        /// </summary>
        public string AdoInvariantForReminders
        {
            get
            {
                return string.IsNullOrWhiteSpace(adoInvariantForReminders) ? AdoInvariant : adoInvariantForReminders;
            }
            set { adoInvariantForReminders = value; }
        }

        internal TimeSpan CollectionQuantum { get; set; }

        /// <summary>
        /// Specifies the maximum time that a request can take before the activation is reported as "blocked"
        /// </summary>
        public TimeSpan MaxRequestProcessingTime { get; set; }

        /// <summary>
        /// The CacheSize attribute specifies the maximum number of grains to cache directory information for.
        /// </summary>
        public int CacheSize { get; set; }
        /// <summary>
        /// The InitialTTL attribute specifies the initial (minimum) time, in seconds, to keep a cache entry before revalidating.
        /// </summary>
        public TimeSpan InitialCacheTTL { get; set; }
        /// <summary>
        /// The MaximumTTL attribute specifies the maximum time, in seconds, to keep a cache entry before revalidating.
        /// </summary>
        public TimeSpan MaximumCacheTTL { get; set; }
        /// <summary>
        /// The TTLExtensionFactor attribute specifies the factor by which cache entry TTLs should be extended when they are found to be stable.
        /// </summary>
        public double CacheTTLExtensionFactor { get; set; }
        /// <summary>
        /// Retry count for Azure Table operations. 
        /// </summary>
        public int MaxStorageBusyRetries { get; private set; }

        /// <summary>
        /// The DirectoryCachingStrategy attribute specifies the caching strategy to use.
        /// The options are None, which means don't cache directory entries locally;
        /// LRU, which indicates that a standard fixed-size least recently used strategy should be used; and
        /// Adaptive, which indicates that an adaptive strategy with a fixed maximum size should be used.
        /// The Adaptive strategy is used by default.
        /// </summary>
        public DirectoryCachingStrategyType DirectoryCachingStrategy { get; set; }

        public bool UseVirtualBucketsConsistentRing { get; set; }
        public int NumVirtualBucketsConsistentRing { get; set; }

        /// <summary>
        /// The LivenessType attribute controls the liveness method used for silo reliability.
        /// </summary>
        private LivenessProviderType livenessServiceType;
        public LivenessProviderType LivenessType
        {
            get
            {
                return livenessServiceType;
            }
            set
            {
                if (value == LivenessProviderType.NotSpecified)
                    throw new ArgumentException("Cannot set LivenessType to " + LivenessProviderType.NotSpecified, "LivenessType");

                livenessServiceType = value;
            }
        }

        /// <summary>
        /// Assembly to use for custom MembershipTable implementation
        /// </summary>
        public string MembershipTableAssembly { get; set; }

        /// <summary>
        /// Assembly to use for custom ReminderTable implementation
        /// </summary>
        public string ReminderTableAssembly { get; set; }

        /// <summary>
        /// The ReminderServiceType attribute controls the type of the reminder service implementation used by silos.
        /// </summary>
        private ReminderServiceProviderType reminderServiceType;
        public ReminderServiceProviderType ReminderServiceType
        {
            get
            {
                return reminderServiceType;
            }
            set
            {
                SetReminderServiceType(value);
            }
        }

        // It's a separate function so we can clearly see when we set the value.
        // With property you can't seaprate getter from setter in intellicense.
        internal void SetReminderServiceType(ReminderServiceProviderType reminderType)
        {
            if (reminderType == ReminderServiceProviderType.NotSpecified)
                throw new ArgumentException("Cannot set ReminderServiceType to " + ReminderServiceProviderType.NotSpecified, "ReminderServiceType");

            reminderServiceType = reminderType;
        }

        public TimeSpan MockReminderTableTimeout { get; set; }
        internal bool UseMockReminderTable;

        /// <summary>
        /// Configuration for various runtime providers.
        /// </summary>
        public IDictionary<string, ProviderCategoryConfiguration> ProviderConfigurations { get; set; }

        /// <summary>
        /// Configuration for grain services.
        /// </summary>
        public GrainServiceConfigurations GrainServiceConfigurations { get; set; }

        /// <summary>
        /// The time span between when we have added an entry for an activation to the grain directory and when we are allowed
        /// to conditionally remove that entry. 
        /// Conditional deregistration is used for lazy clean-up of activations whose prompt deregistration failed for some reason (e.g., message failure).
        /// This should always be at least one minute, since we compare the times on the directory partition, so message delays and clcks skues have
        /// to be allowed.
        /// </summary>
        public TimeSpan DirectoryLazyDeregistrationDelay { get; set; }

        public TimeSpan ClientRegistrationRefresh { get; set; }

        internal bool PerformDeadlockDetection { get; set; }

        public string DefaultPlacementStrategy { get; set; }

        public TimeSpan DeploymentLoadPublisherRefreshTime { get; set; }

        public int ActivationCountBasedPlacementChooseOutOf { get; set; }

        public bool AssumeHomogenousSilosForTesting { get; set; }

        /// <summary>
        /// Determines if ADO should be used for storage of Membership and Reminders info.
        /// True if either or both of LivenessType and ReminderServiceType are set to SqlServer, false otherwise.
        /// </summary>
        internal bool UseSqlSystemStore
        {
            get
            {
                return !String.IsNullOrWhiteSpace(DataConnectionString) && (
                    (LivenessEnabled && LivenessType == LivenessProviderType.SqlServer)
                    || ReminderServiceType == ReminderServiceProviderType.SqlServer);
            }
        }

        /// <summary>
        /// Determines if ZooKeeper should be used for storage of Membership and Reminders info.
        /// True if LivenessType is set to ZooKeeper, false otherwise.
        /// </summary>
        internal bool UseZooKeeperSystemStore
        {
            get
            {
                return !String.IsNullOrWhiteSpace(DataConnectionString) && (
                    (LivenessEnabled && LivenessType == LivenessProviderType.ZooKeeper));
            }
        }

        /// <summary>
        /// Determines if Azure Storage should be used for storage of Membership and Reminders info.
        /// True if either or both of LivenessType and ReminderServiceType are set to AzureTable, false otherwise.
        /// </summary>
        internal bool UseAzureSystemStore
        {
            get
            {
                return !String.IsNullOrWhiteSpace(DataConnectionString)
                       && !UseSqlSystemStore && !UseZooKeeperSystemStore;
            }
        }

        internal bool RunsInAzure { get { return UseAzureSystemStore && !String.IsNullOrWhiteSpace(DeploymentId); } }

        private static readonly TimeSpan DEFAULT_LIVENESS_PROBE_TIMEOUT = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DEFAULT_LIVENESS_TABLE_REFRESH_TIMEOUT = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan DEFAULT_LIVENESS_DEATH_VOTE_EXPIRATION_TIMEOUT = TimeSpan.FromSeconds(120);
        private static readonly TimeSpan DEFAULT_LIVENESS_I_AM_ALIVE_TABLE_PUBLISH_TIMEOUT = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan DEFAULT_LIVENESS_MAX_JOIN_ATTEMPT_TIME = TimeSpan.FromMinutes(5); // 5 min
        private static readonly TimeSpan DEFAULT_REFRESH_CLUSTER_INTERFACEMAP_TIME = TimeSpan.FromMinutes(1);
        private const int DEFAULT_LIVENESS_NUM_MISSED_PROBES_LIMIT = 3;
        private const int DEFAULT_LIVENESS_NUM_PROBED_SILOS = 3;
        private const int DEFAULT_LIVENESS_NUM_VOTES_FOR_DEATH_DECLARATION = 2;
        private const int DEFAULT_LIVENESS_NUM_TABLE_I_AM_ALIVE_LIMIT = 2;
        private const bool DEFAULT_LIVENESS_USE_LIVENESS_GOSSIP = true;
        private const bool DEFAULT_VALIDATE_INITIAL_CONNECTIVITY = true;
        private const int DEFAULT_MAX_MULTICLUSTER_GATEWAYS = 10;
        private const bool DEFAULT_USE_GLOBAL_SINGLE_INSTANCE = true;
        private static readonly TimeSpan DEFAULT_BACKGROUND_GOSSIP_INTERVAL = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DEFAULT_GLOBAL_SINGLE_INSTANCE_RETRY_INTERVAL = TimeSpan.FromSeconds(30);
        private const int DEFAULT_GLOBAL_SINGLE_INSTANCE_NUMBER_RETRIES = 10;
        private const int DEFAULT_LIVENESS_EXPECTED_CLUSTER_SIZE = 20;
        private const int DEFAULT_CACHE_SIZE = 1000000;
        private static readonly TimeSpan DEFAULT_INITIAL_CACHE_TTL = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DEFAULT_MAXIMUM_CACHE_TTL = TimeSpan.FromSeconds(240);
        private const double DEFAULT_TTL_EXTENSION_FACTOR = 2.0;
        private const DirectoryCachingStrategyType DEFAULT_DIRECTORY_CACHING_STRATEGY = DirectoryCachingStrategyType.Adaptive;
        internal static readonly TimeSpan DEFAULT_COLLECTION_QUANTUM = TimeSpan.FromMinutes(1);
        internal static readonly TimeSpan DEFAULT_COLLECTION_AGE_LIMIT = TimeSpan.FromHours(2);
        public static bool ENFORCE_MINIMUM_REQUIREMENT_FOR_AGE_LIMIT = true;
        private static readonly TimeSpan DEFAULT_UNREGISTER_RACE_DELAY = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan DEFAULT_CLIENT_REGISTRATION_REFRESH = TimeSpan.FromMinutes(5);
        public const bool DEFAULT_PERFORM_DEADLOCK_DETECTION = false;
        public static readonly string DEFAULT_PLACEMENT_STRATEGY = typeof(RandomPlacement).Name;
        public static readonly string DEFAULT_MULTICLUSTER_REGISTRATION_STRATEGY = typeof(GlobalSingleInstanceRegistration).Name;
        private static readonly TimeSpan DEFAULT_DEPLOYMENT_LOAD_PUBLISHER_REFRESH_TIME = TimeSpan.FromSeconds(1);
        private const int DEFAULT_ACTIVATION_COUNT_BASED_PLACEMENT_CHOOSE_OUT_OF = 2;

        private const bool DEFAULT_USE_VIRTUAL_RING_BUCKETS = true;
        private const int DEFAULT_NUM_VIRTUAL_RING_BUCKETS = 30;
        private static readonly TimeSpan DEFAULT_MOCK_REMINDER_TABLE_TIMEOUT = TimeSpan.FromMilliseconds(50);
        private string dataConnectionStringForReminders;
        private string adoInvariantForReminders;

        internal GlobalConfiguration()
            : base(true)
        {
            Application = new ApplicationConfiguration();
            SeedNodes = new List<IPEndPoint>();
            livenessServiceType = LivenessProviderType.NotSpecified;
            LivenessEnabled = true;
            ProbeTimeout = DEFAULT_LIVENESS_PROBE_TIMEOUT;
            TableRefreshTimeout = DEFAULT_LIVENESS_TABLE_REFRESH_TIMEOUT;
            DeathVoteExpirationTimeout = DEFAULT_LIVENESS_DEATH_VOTE_EXPIRATION_TIMEOUT;
            IAmAliveTablePublishTimeout = DEFAULT_LIVENESS_I_AM_ALIVE_TABLE_PUBLISH_TIMEOUT;
            NumMissedProbesLimit = DEFAULT_LIVENESS_NUM_MISSED_PROBES_LIMIT;
            NumProbedSilos = DEFAULT_LIVENESS_NUM_PROBED_SILOS;
            NumVotesForDeathDeclaration = DEFAULT_LIVENESS_NUM_VOTES_FOR_DEATH_DECLARATION;
            NumMissedTableIAmAliveLimit = DEFAULT_LIVENESS_NUM_TABLE_I_AM_ALIVE_LIMIT;
            UseLivenessGossip = DEFAULT_LIVENESS_USE_LIVENESS_GOSSIP;
            ValidateInitialConnectivity = DEFAULT_VALIDATE_INITIAL_CONNECTIVITY;
            MaxJoinAttemptTime = DEFAULT_LIVENESS_MAX_JOIN_ATTEMPT_TIME;
            TypeMapRefreshInterval = DEFAULT_REFRESH_CLUSTER_INTERFACEMAP_TIME;
            MaxMultiClusterGateways = DEFAULT_MAX_MULTICLUSTER_GATEWAYS;
            BackgroundGossipInterval = DEFAULT_BACKGROUND_GOSSIP_INTERVAL;
            UseGlobalSingleInstanceByDefault = DEFAULT_USE_GLOBAL_SINGLE_INSTANCE;
            GlobalSingleInstanceRetryInterval = DEFAULT_GLOBAL_SINGLE_INSTANCE_RETRY_INTERVAL;
            GlobalSingleInstanceNumberRetries = DEFAULT_GLOBAL_SINGLE_INSTANCE_NUMBER_RETRIES;
            ExpectedClusterSizeConfigValue = new ConfigValue<int>(DEFAULT_LIVENESS_EXPECTED_CLUSTER_SIZE, true);
            ServiceId = Guid.Empty;
            DeploymentId = "";
            DataConnectionString = "";

            // Assume the ado invariant is for sql server storage if not explicitly specified
            AdoInvariant = Constants.INVARIANT_NAME_SQL_SERVER;

            MaxRequestProcessingTime = DEFAULT_COLLECTION_AGE_LIMIT;
            CollectionQuantum = DEFAULT_COLLECTION_QUANTUM;

            CacheSize = DEFAULT_CACHE_SIZE;
            InitialCacheTTL = DEFAULT_INITIAL_CACHE_TTL;
            MaximumCacheTTL = DEFAULT_MAXIMUM_CACHE_TTL;
            CacheTTLExtensionFactor = DEFAULT_TTL_EXTENSION_FACTOR;
            DirectoryCachingStrategy = DEFAULT_DIRECTORY_CACHING_STRATEGY;
            DirectoryLazyDeregistrationDelay = DEFAULT_UNREGISTER_RACE_DELAY;
            ClientRegistrationRefresh = DEFAULT_CLIENT_REGISTRATION_REFRESH;

            PerformDeadlockDetection = DEFAULT_PERFORM_DEADLOCK_DETECTION;
            reminderServiceType = ReminderServiceProviderType.NotSpecified;
            DefaultPlacementStrategy = DEFAULT_PLACEMENT_STRATEGY;
            DeploymentLoadPublisherRefreshTime = DEFAULT_DEPLOYMENT_LOAD_PUBLISHER_REFRESH_TIME;
            ActivationCountBasedPlacementChooseOutOf = DEFAULT_ACTIVATION_COUNT_BASED_PLACEMENT_CHOOSE_OUT_OF;
            UseVirtualBucketsConsistentRing = DEFAULT_USE_VIRTUAL_RING_BUCKETS;
            NumVirtualBucketsConsistentRing = DEFAULT_NUM_VIRTUAL_RING_BUCKETS;
            UseMockReminderTable = false;
            MockReminderTableTimeout = DEFAULT_MOCK_REMINDER_TABLE_TIMEOUT;
            AssumeHomogenousSilosForTesting = false;

            ProviderConfigurations = new Dictionary<string, ProviderCategoryConfiguration>();
            GrainServiceConfigurations = new GrainServiceConfigurations();
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("   System Ids:").AppendLine();
            sb.AppendFormat("      ServiceId: {0}", ServiceId).AppendLine();
            sb.AppendFormat("      DeploymentId: {0}", DeploymentId).AppendLine();
            sb.Append("   Subnet: ").Append(Subnet == null ? "" : Subnet.ToStrings(x => x.ToString(CultureInfo.InvariantCulture), ".")).AppendLine();
            sb.Append("   Seed nodes: ");
            bool first = true;
            foreach (IPEndPoint node in SeedNodes)
            {
                if (!first)
                {
                    sb.Append(", ");
                }
                sb.Append(node.ToString());
                first = false;
            }
            sb.AppendLine();
            sb.AppendFormat(base.ToString());
            sb.AppendFormat("   Liveness:").AppendLine();
            sb.AppendFormat("      LivenessEnabled: {0}", LivenessEnabled).AppendLine();
            sb.AppendFormat("      LivenessType: {0}", LivenessType).AppendLine();
            sb.AppendFormat("      ProbeTimeout: {0}", ProbeTimeout).AppendLine();
            sb.AppendFormat("      TableRefreshTimeout: {0}", TableRefreshTimeout).AppendLine();
            sb.AppendFormat("      DeathVoteExpirationTimeout: {0}", DeathVoteExpirationTimeout).AppendLine();
            sb.AppendFormat("      NumMissedProbesLimit: {0}", NumMissedProbesLimit).AppendLine();
            sb.AppendFormat("      NumProbedSilos: {0}", NumProbedSilos).AppendLine();
            sb.AppendFormat("      NumVotesForDeathDeclaration: {0}", NumVotesForDeathDeclaration).AppendLine();
            sb.AppendFormat("      UseLivenessGossip: {0}", UseLivenessGossip).AppendLine();
            sb.AppendFormat("      ValidateInitialConnectivity: {0}", ValidateInitialConnectivity).AppendLine();
            sb.AppendFormat("      IAmAliveTablePublishTimeout: {0}", IAmAliveTablePublishTimeout).AppendLine();
            sb.AppendFormat("      NumMissedTableIAmAliveLimit: {0}", NumMissedTableIAmAliveLimit).AppendLine();
            sb.AppendFormat("      MaxJoinAttemptTime: {0}", MaxJoinAttemptTime).AppendLine();
            sb.AppendFormat("      ExpectedClusterSize: {0}", ExpectedClusterSize).AppendLine();

            if (HasMultiClusterNetwork)
            {
                sb.AppendLine("   MultiClusterNetwork:");
                sb.AppendFormat("      ClusterId: {0}", ClusterId ?? "").AppendLine();
                sb.AppendFormat("      DefaultMultiCluster: {0}", DefaultMultiCluster != null ? string.Join(",", DefaultMultiCluster) : "null").AppendLine();
                sb.AppendFormat("      MaxMultiClusterGateways: {0}", MaxMultiClusterGateways).AppendLine();
                sb.AppendFormat("      BackgroundGossipInterval: {0}", BackgroundGossipInterval).AppendLine();
                sb.AppendFormat("      UseGlobalSingleInstanceByDefault: {0}", UseGlobalSingleInstanceByDefault).AppendLine();
                sb.AppendFormat("      GlobalSingleInstanceRetryInterval: {0}", GlobalSingleInstanceRetryInterval).AppendLine();
                sb.AppendFormat("      GlobalSingleInstanceNumberRetries: {0}", GlobalSingleInstanceNumberRetries).AppendLine();
                sb.AppendFormat("      GossipChannels: {0}", string.Join(",", GossipChannels.Select(conf => conf.ChannelType.ToString() + ":" + conf.ConnectionString))).AppendLine();
            }
            else
            {
                sb.AppendLine("   MultiClusterNetwork: N/A");
            }

            sb.AppendFormat("   SystemStore:").AppendLine();
            // Don't print connection credentials in log files, so pass it through redactment filter
            string connectionStringForLog = ConfigUtilities.RedactConnectionStringInfo(DataConnectionString);
            sb.AppendFormat("      SystemStore ConnectionString: {0}", connectionStringForLog).AppendLine();
            string remindersConnectionStringForLog = ConfigUtilities.RedactConnectionStringInfo(DataConnectionStringForReminders);
            sb.AppendFormat("      Reminders ConnectionString: {0}", remindersConnectionStringForLog).AppendLine();
            sb.Append(Application.ToString()).AppendLine();
            sb.Append("   PlacementStrategy: ").AppendLine();
            sb.Append("      ").Append("   Default Placement Strategy: ").Append(DefaultPlacementStrategy).AppendLine();
            sb.Append("      ").Append("   Deployment Load Publisher Refresh Time: ").Append(DeploymentLoadPublisherRefreshTime).AppendLine();
            sb.Append("      ").Append("   Activation CountBased Placement Choose Out Of: ").Append(ActivationCountBasedPlacementChooseOutOf).AppendLine();
            sb.AppendFormat("   Grain directory cache:").AppendLine();
            sb.AppendFormat("      Maximum size: {0} grains", CacheSize).AppendLine();
            sb.AppendFormat("      Initial TTL: {0}", InitialCacheTTL).AppendLine();
            sb.AppendFormat("      Maximum TTL: {0}", MaximumCacheTTL).AppendLine();
            sb.AppendFormat("      TTL extension factor: {0:F2}", CacheTTLExtensionFactor).AppendLine();
            sb.AppendFormat("      Directory Caching Strategy: {0}", DirectoryCachingStrategy).AppendLine();
            sb.AppendFormat("   Grain directory:").AppendLine();
            sb.AppendFormat("      Lazy deregistration delay: {0}", DirectoryLazyDeregistrationDelay).AppendLine();
            sb.AppendFormat("      Client registration refresh: {0}", ClientRegistrationRefresh).AppendLine();
            sb.AppendFormat("   Reminder Service:").AppendLine();
            sb.AppendFormat("       ReminderServiceType: {0}", ReminderServiceType).AppendLine();
            if (ReminderServiceType == ReminderServiceProviderType.MockTable)
            {
                sb.AppendFormat("       MockReminderTableTimeout: {0}ms", MockReminderTableTimeout.TotalMilliseconds).AppendLine();
            }
            sb.AppendFormat("   Consistent Ring:").AppendLine();
            sb.AppendFormat("       Use Virtual Buckets Consistent Ring: {0}", UseVirtualBucketsConsistentRing).AppendLine();
            sb.AppendFormat("       Num Virtual Buckets Consistent Ring: {0}", NumVirtualBucketsConsistentRing).AppendLine();
            sb.AppendFormat("   Providers:").AppendLine();
            sb.Append(ProviderConfigurationUtility.PrintProviderConfigurations(ProviderConfigurations));

            return sb.ToString();
        }

        internal override void Load(XmlElement root)
        {
            var logger = LogManager.GetLogger("OrleansConfiguration", LoggerType.Runtime);
            SeedNodes = new List<IPEndPoint>();

            XmlElement child;
            foreach (XmlNode c in root.ChildNodes)
            {
                child = c as XmlElement;
                if (child != null && child.LocalName == "Networking")
                {
                    Subnet = child.HasAttribute("Subnet")
                        ? ConfigUtilities.ParseSubnet(child.GetAttribute("Subnet"), "Invalid Subnet")
                        : null;
                }
            }
            foreach (XmlNode c in root.ChildNodes)
            {
                child = c as XmlElement;
                if (child == null) continue; // Skip comment lines

                switch (child.LocalName)
                {
                    case "Liveness":
                        if (child.HasAttribute("LivenessEnabled"))
                        {
                            LivenessEnabled = ConfigUtilities.ParseBool(child.GetAttribute("LivenessEnabled"),
                                "Invalid boolean value for the LivenessEnabled attribute on the Liveness element");
                        }
                        if (child.HasAttribute("ProbeTimeout"))
                        {
                            ProbeTimeout = ConfigUtilities.ParseTimeSpan(child.GetAttribute("ProbeTimeout"),
                                "Invalid time value for the ProbeTimeout attribute on the Liveness element");
                        }
                        if (child.HasAttribute("TableRefreshTimeout"))
                        {
                            TableRefreshTimeout = ConfigUtilities.ParseTimeSpan(child.GetAttribute("TableRefreshTimeout"),
                                "Invalid time value for the TableRefreshTimeout attribute on the Liveness element");
                        }
                        if (child.HasAttribute("DeathVoteExpirationTimeout"))
                        {
                            DeathVoteExpirationTimeout = ConfigUtilities.ParseTimeSpan(child.GetAttribute("DeathVoteExpirationTimeout"),
                                "Invalid time value for the DeathVoteExpirationTimeout attribute on the Liveness element");
                        }
                        if (child.HasAttribute("NumMissedProbesLimit"))
                        {
                            NumMissedProbesLimit = ConfigUtilities.ParseInt(child.GetAttribute("NumMissedProbesLimit"),
                                "Invalid integer value for the NumMissedIAmAlive attribute on the Liveness element");
                        }
                        if (child.HasAttribute("NumProbedSilos"))
                        {
                            NumProbedSilos = ConfigUtilities.ParseInt(child.GetAttribute("NumProbedSilos"),
                                "Invalid integer value for the NumProbedSilos attribute on the Liveness element");
                        }
                        if (child.HasAttribute("NumVotesForDeathDeclaration"))
                        {
                            NumVotesForDeathDeclaration = ConfigUtilities.ParseInt(child.GetAttribute("NumVotesForDeathDeclaration"),
                                "Invalid integer value for the NumVotesForDeathDeclaration attribute on the Liveness element");
                        }
                        if (child.HasAttribute("UseLivenessGossip"))
                        {
                            UseLivenessGossip = ConfigUtilities.ParseBool(child.GetAttribute("UseLivenessGossip"),
                                "Invalid boolean value for the UseLivenessGossip attribute on the Liveness element");
                        }
                        if (child.HasAttribute("ValidateInitialConnectivity"))
                        {
                            ValidateInitialConnectivity = ConfigUtilities.ParseBool(child.GetAttribute("ValidateInitialConnectivity"),
                                "Invalid boolean value for the ValidateInitialConnectivity attribute on the Liveness element");
                        }
                        if (child.HasAttribute("IAmAliveTablePublishTimeout"))
                        {
                            IAmAliveTablePublishTimeout = ConfigUtilities.ParseTimeSpan(child.GetAttribute("IAmAliveTablePublishTimeout"),
                                "Invalid time value for the IAmAliveTablePublishTimeout attribute on the Liveness element");
                        }
                        if (child.HasAttribute("NumMissedTableIAmAliveLimit"))
                        {
                            NumMissedTableIAmAliveLimit = ConfigUtilities.ParseInt(child.GetAttribute("NumMissedTableIAmAliveLimit"),
                                "Invalid integer value for the NumMissedTableIAmAliveLimit attribute on the Liveness element");
                        }
                        if (child.HasAttribute("MaxJoinAttemptTime"))
                        {
                            MaxJoinAttemptTime = ConfigUtilities.ParseTimeSpan(child.GetAttribute("MaxJoinAttemptTime"),
                                "Invalid time value for the MaxJoinAttemptTime attribute on the Liveness element");
                        }
                        if (child.HasAttribute("ExpectedClusterSize"))
                        {
                            int expectedClusterSize = ConfigUtilities.ParseInt(child.GetAttribute("ExpectedClusterSize"),
                                "Invalid integer value for the ExpectedClusterSize attribute on the Liveness element");
                            ExpectedClusterSizeConfigValue = new ConfigValue<int>(expectedClusterSize, false);
                        }
                        break;

                    case "Azure":
                    case "SystemStore":
                        if (child.LocalName == "Azure")
                        {
                            // Log warning about deprecated <Azure> element, but then continue on to parse it for connection string info
                            logger.Warn(ErrorCode.SiloConfigDeprecated, "The Azure element has been deprecated -- use SystemStore element instead.");
                        }

                        if (child.HasAttribute("SystemStoreType"))
                        {
                            var sst = child.GetAttribute("SystemStoreType");
                            if (!"None".Equals(sst, StringComparison.OrdinalIgnoreCase))
                            {
                                LivenessType = (LivenessProviderType)Enum.Parse(typeof(LivenessProviderType), sst);
                                ReminderServiceProviderType reminderServiceProviderType;
                                if (LivenessType == LivenessProviderType.MembershipTableGrain)
                                {
                                    // Special case for MembershipTableGrain -> ReminderTableGrain since we use the same setting
                                    // for LivenessType and ReminderServiceType even if the enum are not 100% compatible
                                    reminderServiceProviderType = ReminderServiceProviderType.ReminderTableGrain;
                                }
                                else
                                {
                                    // If LivenessType = ZooKeeper then we set ReminderServiceType to disabled
                                    reminderServiceProviderType = Enum.TryParse(sst, out reminderServiceProviderType)
                                        ? reminderServiceProviderType
                                        : ReminderServiceProviderType.Disabled;
                                }
                                SetReminderServiceType(reminderServiceProviderType);
                            }
                        }
                        if (child.HasAttribute("MembershipTableAssembly"))
                        {
                            MembershipTableAssembly = child.GetAttribute("MembershipTableAssembly");
                            if (LivenessType != LivenessProviderType.Custom)
                                throw new FormatException("SystemStoreType should be \"Custom\" when MembershipTableAssembly is specified");
                            if (MembershipTableAssembly.EndsWith(".dll"))
                                throw new FormatException("Use fully qualified assembly name for \"MembershipTableAssembly\"");
                        }
                        if (child.HasAttribute("ReminderTableAssembly"))
                        {
                            ReminderTableAssembly = child.GetAttribute("ReminderTableAssembly");
                            if (ReminderServiceType != ReminderServiceProviderType.Custom)
                                throw new FormatException("ReminderServiceType should be \"Custom\" when ReminderTableAssembly is specified");
                            if (ReminderTableAssembly.EndsWith(".dll"))
                                throw new FormatException("Use fully qualified assembly name for \"ReminderTableAssembly\"");
                        }
                        if (LivenessType == LivenessProviderType.Custom && string.IsNullOrEmpty(MembershipTableAssembly))
                            throw new FormatException("MembershipTableAssembly should be set when SystemStoreType is \"Custom\"");
                        if (ReminderServiceType == ReminderServiceProviderType.Custom && String.IsNullOrEmpty(ReminderTableAssembly))
                        { 
                            logger.Info("No ReminderTableAssembly specified with SystemStoreType set to Custom: ReminderService will be disabled");
                            SetReminderServiceType(ReminderServiceProviderType.Disabled);
                        }

                        if (child.HasAttribute("ServiceId"))
                        {
                            ServiceId = ConfigUtilities.ParseGuid(child.GetAttribute("ServiceId"),
                                "Invalid Guid value for the ServiceId attribute on the Azure element");
                        }
                        if (child.HasAttribute("DeploymentId"))
                        {
                            DeploymentId = child.GetAttribute("DeploymentId");
                        }
                        if (child.HasAttribute(Constants.DATA_CONNECTION_STRING_NAME))
                        {
                            DataConnectionString = child.GetAttribute(Constants.DATA_CONNECTION_STRING_NAME);
                            if (String.IsNullOrWhiteSpace(DataConnectionString))
                            {
                                throw new FormatException("SystemStore.DataConnectionString cannot be blank");
                            }
                        }
                        if (child.HasAttribute(Constants.DATA_CONNECTION_FOR_REMINDERS_STRING_NAME))
                        {
                            DataConnectionStringForReminders = child.GetAttribute(Constants.DATA_CONNECTION_FOR_REMINDERS_STRING_NAME);
                            if (String.IsNullOrWhiteSpace(DataConnectionStringForReminders))
                            {
                                throw new FormatException("SystemStore.DataConnectionStringForReminders cannot be blank");
                            }
                        }
                        if (child.HasAttribute(Constants.ADO_INVARIANT_NAME))
                        {
                            var adoInvariant = child.GetAttribute(Constants.ADO_INVARIANT_NAME);
                            if (String.IsNullOrWhiteSpace(adoInvariant))
                            {
                                throw new FormatException("SystemStore.AdoInvariant cannot be blank");
                            }
                            AdoInvariant = adoInvariant;
                        }
                        if (child.HasAttribute(Constants.ADO_INVARIANT_FOR_REMINDERS_NAME))
                        {
                            var adoInvariantForReminders = child.GetAttribute(Constants.ADO_INVARIANT_FOR_REMINDERS_NAME);
                            if (String.IsNullOrWhiteSpace(adoInvariantForReminders))
                            {
                                throw new FormatException("SystemStore.adoInvariantForReminders cannot be blank");
                            }
                            AdoInvariantForReminders = adoInvariantForReminders;
                        }
                        if (child.HasAttribute("MaxStorageBusyRetries"))
                        {
                            MaxStorageBusyRetries = ConfigUtilities.ParseInt(child.GetAttribute("MaxStorageBusyRetries"),
                                "Invalid integer value for the MaxStorageBusyRetries attribute on the SystemStore element");
                        }
                        if (child.HasAttribute("UseMockReminderTable"))
                        {
                            MockReminderTableTimeout = ConfigUtilities.ParseTimeSpan(child.GetAttribute("UseMockReminderTable"), "Invalid timeout value");
                            UseMockReminderTable = true;
                        }
                        break;
                    case "MultiClusterNetwork":
                        ClusterId = child.GetAttribute("ClusterId");

                        // we always trim cluster ids to avoid surprises when parsing comma-separated lists
                        if (ClusterId != null) 
                            ClusterId = ClusterId.Trim(); 

                        if (string.IsNullOrEmpty(ClusterId))
                            throw new FormatException("MultiClusterNetwork.ClusterId cannot be blank");
                        if (ClusterId.Contains(","))
                            throw new FormatException("MultiClusterNetwork.ClusterId cannot contain commas: " + ClusterId);

                        if (child.HasAttribute("DefaultMultiCluster"))
                        {
                            var toparse = child.GetAttribute("DefaultMultiCluster").Trim();
                            if (string.IsNullOrEmpty(toparse))
                            {
                                DefaultMultiCluster = new List<string>(); // empty cluster
                            }
                            else
                            {
                                DefaultMultiCluster = toparse.Split(',').Select(id => id.Trim()).ToList();
                                foreach (var id in DefaultMultiCluster)
                                    if (string.IsNullOrEmpty(id))
                                        throw new FormatException("MultiClusterNetwork.DefaultMultiCluster cannot contain blank cluster ids: " + toparse);
                            }
                        }
                        if (child.HasAttribute("BackgroundGossipInterval"))
                        {
                            BackgroundGossipInterval = ConfigUtilities.ParseTimeSpan(child.GetAttribute("BackgroundGossipInterval"),
                                "Invalid time value for the BackgroundGossipInterval attribute on the MultiClusterNetwork element");
                        }
                        if (child.HasAttribute("UseGlobalSingleInstanceByDefault"))
                        {
                            UseGlobalSingleInstanceByDefault = ConfigUtilities.ParseBool(child.GetAttribute("UseGlobalSingleInstanceByDefault"),
                                "Invalid boolean for the UseGlobalSingleInstanceByDefault attribute on the MultiClusterNetwork element");
                        }
                        if (child.HasAttribute("GlobalSingleInstanceRetryInterval"))
                        {
                            GlobalSingleInstanceRetryInterval = ConfigUtilities.ParseTimeSpan(child.GetAttribute("GlobalSingleInstanceRetryInterval"),
                                "Invalid time value for the GlobalSingleInstanceRetryInterval attribute on the MultiClusterNetwork element");
                        }
                        if (child.HasAttribute("GlobalSingleInstanceNumberRetries"))
                        {
                            GlobalSingleInstanceNumberRetries = ConfigUtilities.ParseInt(child.GetAttribute("GlobalSingleInstanceNumberRetries"),
                                "Invalid value for the GlobalSingleInstanceRetryInterval attribute on the MultiClusterNetwork element");
                        }
                        if (child.HasAttribute("MaxMultiClusterGateways"))
                        {
                            MaxMultiClusterGateways = ConfigUtilities.ParseInt(child.GetAttribute("MaxMultiClusterGateways"),
                                "Invalid value for the MaxMultiClusterGateways attribute on the MultiClusterNetwork element");
                        }
                        var channels = new List<GossipChannelConfiguration>();
                        foreach (XmlNode childchild in child.ChildNodes)
                        {
                            var channelspec = childchild as XmlElement;
                            if (channelspec == null || channelspec.LocalName != "GossipChannel")
                                continue;
                            channels.Add(new GossipChannelConfiguration()
                            {
                                ChannelType = (GlobalConfiguration.GossipChannelType)
                                   Enum.Parse(typeof(GlobalConfiguration.GossipChannelType), channelspec.GetAttribute("Type")),
                                ConnectionString = channelspec.GetAttribute("ConnectionString")
                            });
                        }
                        GossipChannels = channels;
                        break;
                    case "SeedNode":
                        SeedNodes.Add(ConfigUtilities.ParseIPEndPoint(child, Subnet).GetResult());
                        break;

                    case "Messaging":
                        base.Load(child);
                        break;

                    case "Application":
                        Application.Load(child, logger);
                        break;

                    case "PlacementStrategy":
                        if (child.HasAttribute("DefaultPlacementStrategy"))
                            DefaultPlacementStrategy = child.GetAttribute("DefaultPlacementStrategy");
                        if (child.HasAttribute("DeploymentLoadPublisherRefreshTime"))
                            DeploymentLoadPublisherRefreshTime = ConfigUtilities.ParseTimeSpan(child.GetAttribute("DeploymentLoadPublisherRefreshTime"),
                                "Invalid time span value for PlacementStrategy.DeploymentLoadPublisherRefreshTime");
                        if (child.HasAttribute("ActivationCountBasedPlacementChooseOutOf"))
                            ActivationCountBasedPlacementChooseOutOf = ConfigUtilities.ParseInt(child.GetAttribute("ActivationCountBasedPlacementChooseOutOf"),
                                "Invalid ActivationCountBasedPlacementChooseOutOf setting");
                        break;

                    case "Caching":
                        if (child.HasAttribute("CacheSize"))
                            CacheSize = ConfigUtilities.ParseInt(child.GetAttribute("CacheSize"),
                                "Invalid integer value for Caching.CacheSize");

                        if (child.HasAttribute("InitialTTL"))
                            InitialCacheTTL = ConfigUtilities.ParseTimeSpan(child.GetAttribute("InitialTTL"),
                                "Invalid time value for Caching.InitialTTL");

                        if (child.HasAttribute("MaximumTTL"))
                            MaximumCacheTTL = ConfigUtilities.ParseTimeSpan(child.GetAttribute("MaximumTTL"),
                                "Invalid time value for Caching.MaximumTTL");

                        if (child.HasAttribute("TTLExtensionFactor"))
                            CacheTTLExtensionFactor = ConfigUtilities.ParseDouble(child.GetAttribute("TTLExtensionFactor"),
                                "Invalid double value for Caching.TTLExtensionFactor");
                        if (CacheTTLExtensionFactor <= 1.0)
                        {
                            throw new FormatException("Caching.TTLExtensionFactor must be greater than 1.0");
                        }

                        if (child.HasAttribute("DirectoryCachingStrategy"))
                            DirectoryCachingStrategy = ConfigUtilities.ParseEnum<DirectoryCachingStrategyType>(child.GetAttribute("DirectoryCachingStrategy"),
                                "Invalid value for Caching.Strategy");

                        break;

                    case "Directory":
                        if (child.HasAttribute("DirectoryLazyDeregistrationDelay"))
                        {
                            DirectoryLazyDeregistrationDelay = ConfigUtilities.ParseTimeSpan(child.GetAttribute("DirectoryLazyDeregistrationDelay"),
                                "Invalid time span value for Directory.DirectoryLazyDeregistrationDelay");
                        }
                        if (child.HasAttribute("ClientRegistrationRefresh"))
                        {
                            ClientRegistrationRefresh = ConfigUtilities.ParseTimeSpan(child.GetAttribute("ClientRegistrationRefresh"),
                                "Invalid time span value for Directory.ClientRegistrationRefresh");
                        }
                        break;

                    default:
                        if (child.LocalName.Equals("GrainServices", StringComparison.Ordinal))
                        {
                            GrainServiceConfigurations = GrainServiceConfigurations.Load(child);
                        }

                        if (child.LocalName.EndsWith("Providers", StringComparison.Ordinal))
                        {
                            var providerCategory = ProviderCategoryConfiguration.Load(child);

                            if (ProviderConfigurations.ContainsKey(providerCategory.Name))
                            {
                                var existingCategory = ProviderConfigurations[providerCategory.Name];
                                existingCategory.Merge(providerCategory);
                            }
                            else
                            {
                                ProviderConfigurations.Add(providerCategory.Name, providerCategory);
                            }
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Registers a given type of <typeparamref name="T"/> where <typeparamref name="T"/> is bootstrap provider
        /// </summary>
        /// <typeparam name="T">Non-abstract type which implements <see cref="IBootstrapProvider"/> interface</typeparam>
        /// <param name="providerName">Name of the bootstrap provider</param>
        /// <param name="properties">Properties that will be passed to bootstrap provider upon initialization</param>
        public void RegisterBootstrapProvider<T>(string providerName, IDictionary<string, string> properties = null) where T : IBootstrapProvider
        {
            Type providerType = typeof(T);
            var providerTypeInfo = providerType.GetTypeInfo();
            if (providerTypeInfo.IsAbstract ||
                providerTypeInfo.IsGenericType ||
                !typeof(IBootstrapProvider).IsAssignableFrom(providerType))
                throw new ArgumentException("Expected non-generic, non-abstract type which implements IBootstrapProvider interface", "typeof(T)");

            ProviderConfigurationUtility.RegisterProvider(ProviderConfigurations, ProviderCategoryConfiguration.BOOTSTRAP_PROVIDER_CATEGORY_NAME, providerTypeInfo.FullName, providerName, properties);
        }

        /// <summary>
        /// Registers a given bootstrap provider.
        /// </summary>
        /// <param name="providerTypeFullName">Full name of the bootstrap provider type</param>
        /// <param name="providerName">Name of the bootstrap provider</param>
        /// <param name="properties">Properties that will be passed to the bootstrap provider upon initialization </param>
        public void RegisterBootstrapProvider(string providerTypeFullName, string providerName, IDictionary<string, string> properties = null)
        {
            ProviderConfigurationUtility.RegisterProvider(ProviderConfigurations, ProviderCategoryConfiguration.BOOTSTRAP_PROVIDER_CATEGORY_NAME, providerTypeFullName, providerName, properties);
        }

        /// <summary>
        /// Registers a given type of <typeparamref name="T"/> where <typeparamref name="T"/> is stream provider
        /// </summary>
        /// <typeparam name="T">Non-abstract type which implements <see cref="IStreamProvider"/> stream</typeparam>
        /// <param name="providerName">Name of the stream provider</param>
        /// <param name="properties">Properties that will be passed to stream provider upon initialization</param>
        public void RegisterStreamProvider<T>(string providerName, IDictionary<string, string> properties = null) where T : Orleans.Streams.IStreamProvider
        {            
            Type providerType = typeof(T);
            var providerTypeInfo = providerType.GetTypeInfo();
            if (providerTypeInfo.IsAbstract ||
                providerTypeInfo.IsGenericType ||
                !typeof(Orleans.Streams.IStreamProvider).IsAssignableFrom(providerType))
                throw new ArgumentException("Expected non-generic, non-abstract type which implements IStreamProvider interface", "typeof(T)");

            ProviderConfigurationUtility.RegisterProvider(ProviderConfigurations, ProviderCategoryConfiguration.STREAM_PROVIDER_CATEGORY_NAME, providerType.FullName, providerName, properties);
        }

        /// <summary>
        /// Registers a given stream provider.
        /// </summary>
        /// <param name="providerTypeFullName">Full name of the stream provider type</param>
        /// <param name="providerName">Name of the stream provider</param>
        /// <param name="properties">Properties that will be passed to the stream provider upon initialization </param>
        public void RegisterStreamProvider(string providerTypeFullName, string providerName, IDictionary<string, string> properties = null)
        {
            ProviderConfigurationUtility.RegisterProvider(ProviderConfigurations, ProviderCategoryConfiguration.STREAM_PROVIDER_CATEGORY_NAME, providerTypeFullName, providerName, properties);
        }

        /// <summary>
        /// Registers a given type of <typeparamref name="T"/> where <typeparamref name="T"/> is storage provider
        /// </summary>
        /// <typeparam name="T">Non-abstract type which implements <see cref="IStorageProvider"/> storage</typeparam>
        /// <param name="providerName">Name of the storage provider</param>
        /// <param name="properties">Properties that will be passed to storage provider upon initialization</param>
        public void RegisterStorageProvider<T>(string providerName, IDictionary<string, string> properties = null) where T : IStorageProvider
        {
            Type providerType = typeof(T);
            var providerTypeInfo = providerType.GetTypeInfo();
            if (providerTypeInfo.IsAbstract ||
                providerTypeInfo.IsGenericType ||
                !typeof(IStorageProvider).IsAssignableFrom(providerType))
                throw new ArgumentException("Expected non-generic, non-abstract type which implements IStorageProvider interface", "typeof(T)");

            ProviderConfigurationUtility.RegisterProvider(ProviderConfigurations, ProviderCategoryConfiguration.STORAGE_PROVIDER_CATEGORY_NAME, providerTypeInfo.FullName, providerName, properties);
        }

        /// <summary>
        /// Registers a given storage provider.
        /// </summary>
        /// <param name="providerTypeFullName">Full name of the storage provider type</param>
        /// <param name="providerName">Name of the storage provider</param>
        /// <param name="properties">Properties that will be passed to the storage provider upon initialization </param>
        public void RegisterStorageProvider(string providerTypeFullName, string providerName, IDictionary<string, string> properties = null)
        {
            ProviderConfigurationUtility.RegisterProvider(ProviderConfigurations, ProviderCategoryConfiguration.STORAGE_PROVIDER_CATEGORY_NAME, providerTypeFullName, providerName, properties);
        }

        public void RegisterStatisticsProvider<T>(string providerName, IDictionary<string, string> properties = null) where T : IStatisticsPublisher, ISiloMetricsDataPublisher
        {
            Type providerType = typeof(T);
            var providerTypeInfo = providerType.GetTypeInfo();
            if (providerTypeInfo.IsAbstract ||
                providerTypeInfo.IsGenericType ||
                !(
                typeof(IStatisticsPublisher).IsAssignableFrom(providerType) &&
                typeof(ISiloMetricsDataPublisher).IsAssignableFrom(providerType)
                ))
                throw new ArgumentException("Expected non-generic, non-abstract type which implements IStatisticsPublisher, ISiloMetricsDataPublisher interface", "typeof(T)");

            ProviderConfigurationUtility.RegisterProvider(ProviderConfigurations, ProviderCategoryConfiguration.STATISTICS_PROVIDER_CATEGORY_NAME, providerTypeInfo.FullName, providerName, properties);
        }

        public void RegisterStatisticsProvider(string providerTypeFullName, string providerName, IDictionary<string, string> properties = null)
        {
            ProviderConfigurationUtility.RegisterProvider(ProviderConfigurations, ProviderCategoryConfiguration.STATISTICS_PROVIDER_CATEGORY_NAME, providerTypeFullName, providerName, properties);
        }

        /// <summary>
        /// Registers a given log-consistency provider.
        /// </summary>
        /// <param name="providerTypeFullName">Full name of the log-consistency provider type</param>
        /// <param name="providerName">Name of the log-consistency provider</param>
        /// <param name="properties">Properties that will be passed to the log-consistency provider upon initialization </param>
        public void RegisterLogConsistencyProvider(string providerTypeFullName, string providerName, IDictionary<string, string> properties = null)
        {
            ProviderConfigurationUtility.RegisterProvider(ProviderConfigurations, ProviderCategoryConfiguration.LOG_CONSISTENCY_PROVIDER_CATEGORY_NAME, providerTypeFullName, providerName, properties);
        }


        /// <summary>
        /// Registers a given type of <typeparamref name="T"/> where <typeparamref name="T"/> is a log-consistency provider
        /// </summary>
        /// <typeparam name="T">Non-abstract type which implements <see cref="ILogConsistencyProvider"/> a log-consistency storage interface</typeparam>
        /// <param name="providerName">Name of the log-consistency provider</param>
        /// <param name="properties">Properties that will be passed to log-consistency provider upon initialization</param>
        public void RegisterLogConsistencyProvider<T>(string providerName, IDictionary<string, string> properties = null) where T : ILogConsistencyProvider
        {
            Type providerType = typeof(T);
            var providerTypeInfo = providerType.GetTypeInfo();
            if (providerTypeInfo.IsAbstract ||
                providerTypeInfo.IsGenericType ||
                !typeof(ILogConsistencyProvider).IsAssignableFrom(providerType))
                throw new ArgumentException("Expected non-generic, non-abstract type which implements ILogConsistencyProvider interface", "typeof(T)");

            ProviderConfigurationUtility.RegisterProvider(ProviderConfigurations, ProviderCategoryConfiguration.LOG_CONSISTENCY_PROVIDER_CATEGORY_NAME, providerType.FullName, providerName, properties);
        } 
        

        /// <summary>
        /// Retrieves an existing provider configuration
        /// </summary>
        /// <param name="providerTypeFullName">Full name of the stream provider type</param>
        /// <param name="providerName">Name of the stream provider</param>
        /// <param name="config">The provider configuration, if exists</param>
        /// <returns>True if a configuration for this provider already exists, false otherwise.</returns>
        public bool TryGetProviderConfiguration(string providerTypeFullName, string providerName, out IProviderConfiguration config)
        {
            return ProviderConfigurationUtility.TryGetProviderConfiguration(ProviderConfigurations, providerTypeFullName, providerName, out config);
        }

        /// <summary>
        /// Retrieves an enumeration of all currently configured provider configurations.
        /// </summary>
        /// <returns>An enumeration of all currently configured provider configurations.</returns>
        public IEnumerable<IProviderConfiguration> GetAllProviderConfigurations()
        {
            return ProviderConfigurationUtility.GetAllProviderConfigurations(ProviderConfigurations);
        }

        public void RegisterGrainService(string serviceName, string serviceType, IDictionary<string, string> properties = null)
        {
            GrainServiceConfigurationsUtility.RegisterGrainService(GrainServiceConfigurations, serviceName, serviceType, properties);
        }
    }
}
