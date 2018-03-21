using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Xml;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.Providers;
using Orleans.Storage;
using Orleans.LogConsistency;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

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
            this.Value = val;
            this.IsDefaultValue = isDefaultValue;
        }
    }

    /// <summary>
    /// Data object holding Silo global configuration parameters.
    /// </summary>
    [Serializable]
    public class GlobalConfiguration : MessagingConfiguration
    {
        private const string DefaultClusterId = "DefaultClusterID"; // if no id is configured, we pick a nonempty default.

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
            /// <summary>ADO.NET is used to store membership information. 
            /// This option can be used in production.</summary>
            AdoNet,
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
            /// <summary>ADO.NET is used to store reminders information. 
            /// This option can be used in production.</summary>
            AdoNet,
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

        public static string Remap(GossipChannelType type)
        {
            switch (type)
            {
                case GossipChannelType.NotSpecified:
                    return MultiClusterOptions.BuiltIn.NotSpecified;
                case GossipChannelType.AzureTable:
                    return MultiClusterOptions.BuiltIn.AzureTable;
                default:
                    throw new NotSupportedException($"GossipChannelType {type} is not supported");
            }
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
            get { return this.LivenessType == LivenessProviderType.MembershipTableGrain; }
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
        public int ExpectedClusterSize { get { return this.ExpectedClusterSizeConfigValue.Value; } set { this.ExpectedClusterSizeConfigValue = new ConfigValue<int>(value, false); } }
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
        /// Cluster identity. Silos with the same cluster identity will join together. 
        /// When deploying different versions of the application simultaneously, be sure to change the ID if they should not join the same logical cluster.
        /// In a multi-cluster network, the cluster ID must be unique for each cluster.
        /// </summary>
        public string ClusterId { get; set; }

        /// <summary>
        /// Deployment Id. This is the same as ClusterId and has been deprecated in favor of it.
        /// </summary>
        [Obsolete(ClientConfiguration.DEPRECATE_DEPLOYMENT_ID_MESSAGE)]
        public string DeploymentId
        {
            get => this.ClusterId;
            set => this.ClusterId = value;
        }

        #region MultiClusterNetwork

        /// <summary>
        /// Whether this cluster is configured to be part of a multicluster network
        /// </summary>
        public bool HasMultiClusterNetwork { get; set; }

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
                return string.IsNullOrWhiteSpace(this.dataConnectionStringForReminders) ? this.DataConnectionString : this.dataConnectionStringForReminders;
            }
            set { this.dataConnectionStringForReminders = value; }
        }

        /// <summary>
        /// Set this property to override <see cref="AdoInvariant"/> for reminders.
        /// </summary>
        public string AdoInvariantForReminders
        {
            get
            {
                return string.IsNullOrWhiteSpace(this.adoInvariantForReminders) ? this.AdoInvariant : this.adoInvariantForReminders;
            }
            set { this.adoInvariantForReminders = value; }
        }

        public TimeSpan CollectionQuantum { get; set; }

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
                return this.livenessServiceType;
            }
            set
            {
                if (value == LivenessProviderType.NotSpecified)
                    throw new ArgumentException("Cannot set LivenessType to " + LivenessProviderType.NotSpecified, "LivenessType");

                this.livenessServiceType = value;
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
                return this.reminderServiceType;
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

            this.reminderServiceType = reminderType;
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

        public bool PerformDeadlockDetection { get; set; }
        
        public bool AllowCallChainReentrancy { get; set; }
        
        public string DefaultPlacementStrategy { get; set; }

        public CompatibilityStrategy DefaultCompatibilityStrategy { get; set; }

        public VersionSelectorStrategy DefaultVersionSelectorStrategy { get; set; }

        public TimeSpan DeploymentLoadPublisherRefreshTime { get; set; }

        public int ActivationCountBasedPlacementChooseOutOf { get; set; }

        public bool AssumeHomogenousSilosForTesting { get; set; }

        /// <summary>
        /// Determines if ADO should be used for storage of Membership and Reminders info.
        /// True if either or both of LivenessType and ReminderServiceType are set to SqlServer, false otherwise.
        /// </summary>
        public bool UseAdoNetSystemStore
        {
            get
            {
                return !string.IsNullOrWhiteSpace(this.DataConnectionString) && (
                    (this.LivenessEnabled && this.LivenessType == LivenessProviderType.AdoNet)
                    || this.ReminderServiceType == ReminderServiceProviderType.AdoNet);
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
                return !string.IsNullOrWhiteSpace(this.DataConnectionString) && (
                    (this.LivenessEnabled && this.LivenessType == LivenessProviderType.ZooKeeper));
            }
        }

        /// <summary>
        /// Determines if Azure Storage should be used for storage of Membership and Reminders info.
        /// True if either or both of LivenessType and ReminderServiceType are set to AzureTable, false otherwise.
        /// </summary>
        public bool UseAzureSystemStore
        {
            get
            {
                return !string.IsNullOrWhiteSpace(this.DataConnectionString)
                       && !this.UseAdoNetSystemStore && !this.UseZooKeeperSystemStore;
            }
        }

        internal bool RunsInAzure { get { return this.UseAzureSystemStore && !string.IsNullOrWhiteSpace(this.ClusterId); } }

        private const int DEFAULT_LIVENESS_EXPECTED_CLUSTER_SIZE = 20;
        public static bool ENFORCE_MINIMUM_REQUIREMENT_FOR_AGE_LIMIT = true;
        public static readonly string DEFAULT_MULTICLUSTER_REGISTRATION_STRATEGY = typeof(GlobalSingleInstanceRegistration).Name;

        private string dataConnectionStringForReminders;
        private string adoInvariantForReminders;

        public GlobalConfiguration()
            : base(true)
        {
            this.Application = new ApplicationConfiguration();
            this.SeedNodes = new List<IPEndPoint>();
            this.livenessServiceType = LivenessProviderType.NotSpecified;
            this.LivenessEnabled = ClusterMembershipOptions.DEFAULT_LIVENESS_ENABLED;
            this.ProbeTimeout = ClusterMembershipOptions.DEFAULT_LIVENESS_PROBE_TIMEOUT;
            this.TableRefreshTimeout = ClusterMembershipOptions.DEFAULT_LIVENESS_TABLE_REFRESH_TIMEOUT;
            this.DeathVoteExpirationTimeout = ClusterMembershipOptions.DEFAULT_LIVENESS_DEATH_VOTE_EXPIRATION_TIMEOUT;
            this.IAmAliveTablePublishTimeout = ClusterMembershipOptions.DEFAULT_LIVENESS_I_AM_ALIVE_TABLE_PUBLISH_TIMEOUT;
            this.NumMissedProbesLimit = ClusterMembershipOptions.DEFAULT_LIVENESS_NUM_MISSED_PROBES_LIMIT;
            this.NumProbedSilos = ClusterMembershipOptions.DEFAULT_LIVENESS_NUM_PROBED_SILOS;
            this.NumVotesForDeathDeclaration = ClusterMembershipOptions.DEFAULT_LIVENESS_NUM_VOTES_FOR_DEATH_DECLARATION;
            this.NumMissedTableIAmAliveLimit = ClusterMembershipOptions.DEFAULT_LIVENESS_NUM_TABLE_I_AM_ALIVE_LIMIT;
            this.UseLivenessGossip = ClusterMembershipOptions.DEFAULT_LIVENESS_USE_LIVENESS_GOSSIP;
            this.ValidateInitialConnectivity = ClusterMembershipOptions.DEFAULT_VALIDATE_INITIAL_CONNECTIVITY;
            this.MaxJoinAttemptTime = ClusterMembershipOptions.DEFAULT_LIVENESS_MAX_JOIN_ATTEMPT_TIME;
            this.TypeMapRefreshInterval = TypeManagementOptions.DEFAULT_REFRESH_CLUSTER_INTERFACEMAP_TIME;
            this.MaxMultiClusterGateways = MultiClusterOptions.DEFAULT_MAX_MULTICLUSTER_GATEWAYS;
            this.BackgroundGossipInterval = MultiClusterOptions.DEFAULT_BACKGROUND_GOSSIP_INTERVAL;
            this.UseGlobalSingleInstanceByDefault = MultiClusterOptions.DEFAULT_USE_GLOBAL_SINGLE_INSTANCE;
            this.GlobalSingleInstanceRetryInterval = MultiClusterOptions.DEFAULT_GLOBAL_SINGLE_INSTANCE_RETRY_INTERVAL;
            this.GlobalSingleInstanceNumberRetries = MultiClusterOptions.DEFAULT_GLOBAL_SINGLE_INSTANCE_NUMBER_RETRIES;
            this.ExpectedClusterSizeConfigValue = new ConfigValue<int>(DEFAULT_LIVENESS_EXPECTED_CLUSTER_SIZE, true);
            this.ServiceId = Guid.Empty;
            this.ClusterId = "";
            this.DataConnectionString = "";

            // Assume the ado invariant is for sql server storage if not explicitly specified
            this.AdoInvariant = Constants.INVARIANT_NAME_SQL_SERVER;

            this.MaxRequestProcessingTime = TimeSpan.FromHours(2);
            this.CollectionQuantum = TimeSpan.FromMinutes(1);

            this.CacheSize = 1000000;
            this.InitialCacheTTL = TimeSpan.FromSeconds(30);
            this.MaximumCacheTTL = TimeSpan.FromSeconds(240);
            this.CacheTTLExtensionFactor = 2.0;
            this.DirectoryCachingStrategy = DirectoryCachingStrategyType.Adaptive;
            this.DirectoryLazyDeregistrationDelay = TimeSpan.FromMinutes(1);
            this.ClientRegistrationRefresh = TimeSpan.FromMinutes(5);

            this.PerformDeadlockDetection = false;
            this.AllowCallChainReentrancy = false;
            this.reminderServiceType = ReminderServiceProviderType.NotSpecified;
            this.DefaultPlacementStrategy = nameof(RandomPlacement);
            this.DeploymentLoadPublisherRefreshTime = TimeSpan.FromSeconds(1);
            this.ActivationCountBasedPlacementChooseOutOf = 2;
            this.UseVirtualBucketsConsistentRing = true;
            this.NumVirtualBucketsConsistentRing = 30;
            this.UseMockReminderTable = false;
            this.MockReminderTableTimeout = TimeSpan.FromMilliseconds(50);
            this.AssumeHomogenousSilosForTesting = false;

            this.ProviderConfigurations = new Dictionary<string, ProviderCategoryConfiguration>();
            this.GrainServiceConfigurations = new GrainServiceConfigurations();
            this.DefaultCompatibilityStrategy = BackwardCompatible.Singleton;
            this.DefaultVersionSelectorStrategy = AllCompatibleVersions.Singleton;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("   System Ids:").AppendLine();
            sb.AppendFormat("      ServiceId: {0}", this.ServiceId).AppendLine();
            sb.AppendFormat("      ClusterId: {0}", this.ClusterId).AppendLine();
            sb.Append("   Subnet: ").Append(this.Subnet == null ? "" : this.Subnet.ToStrings(x => x.ToString(CultureInfo.InvariantCulture), ".")).AppendLine();
            sb.Append("   Seed nodes: ");
            bool first = true;
            foreach (IPEndPoint node in this.SeedNodes)
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
            sb.AppendFormat("      LivenessEnabled: {0}", this.LivenessEnabled).AppendLine();
            sb.AppendFormat("      LivenessType: {0}", this.LivenessType).AppendLine();
            sb.AppendFormat("      ProbeTimeout: {0}", this.ProbeTimeout).AppendLine();
            sb.AppendFormat("      TableRefreshTimeout: {0}", this.TableRefreshTimeout).AppendLine();
            sb.AppendFormat("      DeathVoteExpirationTimeout: {0}", this.DeathVoteExpirationTimeout).AppendLine();
            sb.AppendFormat("      NumMissedProbesLimit: {0}", this.NumMissedProbesLimit).AppendLine();
            sb.AppendFormat("      NumProbedSilos: {0}", this.NumProbedSilos).AppendLine();
            sb.AppendFormat("      NumVotesForDeathDeclaration: {0}", this.NumVotesForDeathDeclaration).AppendLine();
            sb.AppendFormat("      UseLivenessGossip: {0}", this.UseLivenessGossip).AppendLine();
            sb.AppendFormat("      ValidateInitialConnectivity: {0}", this.ValidateInitialConnectivity).AppendLine();
            sb.AppendFormat("      IAmAliveTablePublishTimeout: {0}", this.IAmAliveTablePublishTimeout).AppendLine();
            sb.AppendFormat("      NumMissedTableIAmAliveLimit: {0}", this.NumMissedTableIAmAliveLimit).AppendLine();
            sb.AppendFormat("      MaxJoinAttemptTime: {0}", this.MaxJoinAttemptTime).AppendLine();
            sb.AppendFormat("      ExpectedClusterSize: {0}", this.ExpectedClusterSize).AppendLine();

            if (this.HasMultiClusterNetwork)
            {
                sb.AppendLine("   MultiClusterNetwork:");
                sb.AppendFormat("      ClusterId: {0}", this.ClusterId ?? "").AppendLine();
                sb.AppendFormat("      DefaultMultiCluster: {0}", this.DefaultMultiCluster != null ? string.Join(",", this.DefaultMultiCluster) : "null").AppendLine();
                sb.AppendFormat("      MaxMultiClusterGateways: {0}", this.MaxMultiClusterGateways).AppendLine();
                sb.AppendFormat("      BackgroundGossipInterval: {0}", this.BackgroundGossipInterval).AppendLine();
                sb.AppendFormat("      UseGlobalSingleInstanceByDefault: {0}", this.UseGlobalSingleInstanceByDefault).AppendLine();
                sb.AppendFormat("      GlobalSingleInstanceRetryInterval: {0}", this.GlobalSingleInstanceRetryInterval).AppendLine();
                sb.AppendFormat("      GlobalSingleInstanceNumberRetries: {0}", this.GlobalSingleInstanceNumberRetries).AppendLine();
                sb.AppendFormat("      GossipChannels: {0}", string.Join(",", this.GossipChannels.Select(conf => conf.ChannelType.ToString() + ":" + conf.ConnectionString))).AppendLine();
            }
            else
            {
                sb.AppendLine("   MultiClusterNetwork: N/A");
            }

            sb.AppendFormat("   SystemStore:").AppendLine();
            // Don't print connection credentials in log files, so pass it through redactment filter
            string connectionStringForLog = ConfigUtilities.RedactConnectionStringInfo(this.DataConnectionString);
            sb.AppendFormat("      SystemStore ConnectionString: {0}", connectionStringForLog).AppendLine();
            string remindersConnectionStringForLog = ConfigUtilities.RedactConnectionStringInfo(this.DataConnectionStringForReminders);
            sb.AppendFormat("      Reminders ConnectionString: {0}", remindersConnectionStringForLog).AppendLine();
            sb.Append(this.Application.ToString()).AppendLine();
            sb.Append("   PlacementStrategy: ").AppendLine();
            sb.Append("      ").Append("   Default Placement Strategy: ").Append(this.DefaultPlacementStrategy).AppendLine();
            sb.Append("      ").Append("   Deployment Load Publisher Refresh Time: ").Append(this.DeploymentLoadPublisherRefreshTime).AppendLine();
            sb.Append("      ").Append("   Activation CountBased Placement Choose Out Of: ").Append(this.ActivationCountBasedPlacementChooseOutOf).AppendLine();
            sb.AppendFormat("   Grain directory cache:").AppendLine();
            sb.AppendFormat("      Maximum size: {0} grains", this.CacheSize).AppendLine();
            sb.AppendFormat("      Initial TTL: {0}", this.InitialCacheTTL).AppendLine();
            sb.AppendFormat("      Maximum TTL: {0}", this.MaximumCacheTTL).AppendLine();
            sb.AppendFormat("      TTL extension factor: {0:F2}", this.CacheTTLExtensionFactor).AppendLine();
            sb.AppendFormat("      Directory Caching Strategy: {0}", this.DirectoryCachingStrategy).AppendLine();
            sb.AppendFormat("   Grain directory:").AppendLine();
            sb.AppendFormat("      Lazy deregistration delay: {0}", this.DirectoryLazyDeregistrationDelay).AppendLine();
            sb.AppendFormat("      Client registration refresh: {0}", this.ClientRegistrationRefresh).AppendLine();
            sb.AppendFormat("   Reminder Service:").AppendLine();
            sb.AppendFormat("       ReminderServiceType: {0}", this.ReminderServiceType).AppendLine();
            if (this.ReminderServiceType == ReminderServiceProviderType.MockTable)
            {
                sb.AppendFormat("       MockReminderTableTimeout: {0}ms", this.MockReminderTableTimeout.TotalMilliseconds).AppendLine();
            }
            sb.AppendFormat("   Consistent Ring:").AppendLine();
            sb.AppendFormat("       Use Virtual Buckets Consistent Ring: {0}", this.UseVirtualBucketsConsistentRing).AppendLine();
            sb.AppendFormat("       Num Virtual Buckets Consistent Ring: {0}", this.NumVirtualBucketsConsistentRing).AppendLine();
            sb.AppendFormat("   Providers:").AppendLine();
            sb.Append(ProviderConfigurationUtility.PrintProviderConfigurations(this.ProviderConfigurations));

            return sb.ToString();
        }

        internal override void Load(XmlElement root)
        {
            this.SeedNodes = new List<IPEndPoint>();

            XmlElement child;
            foreach (XmlNode c in root.ChildNodes)
            {
                child = c as XmlElement;
                if (child != null && child.LocalName == "Networking")
                {
                    this.Subnet = child.HasAttribute("Subnet")
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
                            this.LivenessEnabled = ConfigUtilities.ParseBool(child.GetAttribute("LivenessEnabled"),
                                "Invalid boolean value for the LivenessEnabled attribute on the Liveness element");
                        }
                        if (child.HasAttribute("ProbeTimeout"))
                        {
                            this.ProbeTimeout = ConfigUtilities.ParseTimeSpan(child.GetAttribute("ProbeTimeout"),
                                "Invalid time value for the ProbeTimeout attribute on the Liveness element");
                        }
                        if (child.HasAttribute("TableRefreshTimeout"))
                        {
                            this.TableRefreshTimeout = ConfigUtilities.ParseTimeSpan(child.GetAttribute("TableRefreshTimeout"),
                                "Invalid time value for the TableRefreshTimeout attribute on the Liveness element");
                        }
                        if (child.HasAttribute("DeathVoteExpirationTimeout"))
                        {
                            this.DeathVoteExpirationTimeout = ConfigUtilities.ParseTimeSpan(child.GetAttribute("DeathVoteExpirationTimeout"),
                                "Invalid time value for the DeathVoteExpirationTimeout attribute on the Liveness element");
                        }
                        if (child.HasAttribute("NumMissedProbesLimit"))
                        {
                            this.NumMissedProbesLimit = ConfigUtilities.ParseInt(child.GetAttribute("NumMissedProbesLimit"),
                                "Invalid integer value for the NumMissedIAmAlive attribute on the Liveness element");
                        }
                        if (child.HasAttribute("NumProbedSilos"))
                        {
                            this.NumProbedSilos = ConfigUtilities.ParseInt(child.GetAttribute("NumProbedSilos"),
                                "Invalid integer value for the NumProbedSilos attribute on the Liveness element");
                        }
                        if (child.HasAttribute("NumVotesForDeathDeclaration"))
                        {
                            this.NumVotesForDeathDeclaration = ConfigUtilities.ParseInt(child.GetAttribute("NumVotesForDeathDeclaration"),
                                "Invalid integer value for the NumVotesForDeathDeclaration attribute on the Liveness element");
                        }
                        if (child.HasAttribute("UseLivenessGossip"))
                        {
                            this.UseLivenessGossip = ConfigUtilities.ParseBool(child.GetAttribute("UseLivenessGossip"),
                                "Invalid boolean value for the UseLivenessGossip attribute on the Liveness element");
                        }
                        if (child.HasAttribute("ValidateInitialConnectivity"))
                        {
                            this.ValidateInitialConnectivity = ConfigUtilities.ParseBool(child.GetAttribute("ValidateInitialConnectivity"),
                                "Invalid boolean value for the ValidateInitialConnectivity attribute on the Liveness element");
                        }
                        if (child.HasAttribute("IAmAliveTablePublishTimeout"))
                        {
                            this.IAmAliveTablePublishTimeout = ConfigUtilities.ParseTimeSpan(child.GetAttribute("IAmAliveTablePublishTimeout"),
                                "Invalid time value for the IAmAliveTablePublishTimeout attribute on the Liveness element");
                        }
                        if (child.HasAttribute("NumMissedTableIAmAliveLimit"))
                        {
                            this.NumMissedTableIAmAliveLimit = ConfigUtilities.ParseInt(child.GetAttribute("NumMissedTableIAmAliveLimit"),
                                "Invalid integer value for the NumMissedTableIAmAliveLimit attribute on the Liveness element");
                        }
                        if (child.HasAttribute("MaxJoinAttemptTime"))
                        {
                            this.MaxJoinAttemptTime = ConfigUtilities.ParseTimeSpan(child.GetAttribute("MaxJoinAttemptTime"),
                                "Invalid time value for the MaxJoinAttemptTime attribute on the Liveness element");
                        }
                        if (child.HasAttribute("ExpectedClusterSize"))
                        {
                            int expectedClusterSize = ConfigUtilities.ParseInt(child.GetAttribute("ExpectedClusterSize"),
                                "Invalid integer value for the ExpectedClusterSize attribute on the Liveness element");
                            this.ExpectedClusterSizeConfigValue = new ConfigValue<int>(expectedClusterSize, false);
                        }
                        break;

                    case "Azure":
                    case "SystemStore":

                        if (child.HasAttribute("SystemStoreType"))
                        {
                            var sst = child.GetAttribute("SystemStoreType");
                            if (!"None".Equals(sst, StringComparison.OrdinalIgnoreCase))
                            {
                                this.LivenessType = (LivenessProviderType)Enum.Parse(typeof(LivenessProviderType), sst);
                                ReminderServiceProviderType reminderServiceProviderType;
                                if (this.LivenessType == LivenessProviderType.MembershipTableGrain)
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
                            this.MembershipTableAssembly = child.GetAttribute("MembershipTableAssembly");
                            if (this.LivenessType != LivenessProviderType.Custom)
                                throw new FormatException("SystemStoreType should be \"Custom\" when MembershipTableAssembly is specified");
                            if (this.MembershipTableAssembly.EndsWith(".dll"))
                                throw new FormatException("Use fully qualified assembly name for \"MembershipTableAssembly\"");
                        }
                        if (child.HasAttribute("ReminderTableAssembly"))
                        {
                            this.ReminderTableAssembly = child.GetAttribute("ReminderTableAssembly");
                            if (this.ReminderServiceType != ReminderServiceProviderType.Custom)
                                throw new FormatException("ReminderServiceType should be \"Custom\" when ReminderTableAssembly is specified");
                            if (this.ReminderTableAssembly.EndsWith(".dll"))
                                throw new FormatException("Use fully qualified assembly name for \"ReminderTableAssembly\"");
                        }
                        if (this.LivenessType == LivenessProviderType.Custom && string.IsNullOrEmpty(this.MembershipTableAssembly))
                            throw new FormatException("MembershipTableAssembly should be set when SystemStoreType is \"Custom\"");
                        if (this.ReminderServiceType == ReminderServiceProviderType.Custom && string.IsNullOrEmpty(this.ReminderTableAssembly))
                        { 
                            SetReminderServiceType(ReminderServiceProviderType.Disabled);
                        }

                        if (child.HasAttribute("ServiceId"))
                        {
                            this.ServiceId = ConfigUtilities.ParseGuid(child.GetAttribute("ServiceId"),
                                "Invalid Guid value for the ServiceId attribute on the Azure element");
                        }
                        if (child.HasAttribute("DeploymentId"))
                        {
                            this.ClusterId = child.GetAttribute("DeploymentId");
                        }
                        if (child.HasAttribute(Constants.DATA_CONNECTION_STRING_NAME))
                        {
                            this.DataConnectionString = child.GetAttribute(Constants.DATA_CONNECTION_STRING_NAME);
                            if (string.IsNullOrWhiteSpace(this.DataConnectionString))
                            {
                                throw new FormatException("SystemStore.DataConnectionString cannot be blank");
                            }
                        }
                        if (child.HasAttribute(Constants.DATA_CONNECTION_FOR_REMINDERS_STRING_NAME))
                        {
                            this.DataConnectionStringForReminders = child.GetAttribute(Constants.DATA_CONNECTION_FOR_REMINDERS_STRING_NAME);
                            if (string.IsNullOrWhiteSpace(this.DataConnectionStringForReminders))
                            {
                                throw new FormatException("SystemStore.DataConnectionStringForReminders cannot be blank");
                            }
                        }
                        if (child.HasAttribute(Constants.ADO_INVARIANT_NAME))
                        {
                            var adoInvariant = child.GetAttribute(Constants.ADO_INVARIANT_NAME);
                            if (string.IsNullOrWhiteSpace(adoInvariant))
                            {
                                throw new FormatException("SystemStore.AdoInvariant cannot be blank");
                            }
                            this.AdoInvariant = adoInvariant;
                        }
                        if (child.HasAttribute(Constants.ADO_INVARIANT_FOR_REMINDERS_NAME))
                        {
                            var adoInvariantForReminders = child.GetAttribute(Constants.ADO_INVARIANT_FOR_REMINDERS_NAME);
                            if (string.IsNullOrWhiteSpace(adoInvariantForReminders))
                            {
                                throw new FormatException("SystemStore.adoInvariantForReminders cannot be blank");
                            }
                            this.AdoInvariantForReminders = adoInvariantForReminders;
                        }
                        if (child.HasAttribute("MaxStorageBusyRetries"))
                        {
                            this.MaxStorageBusyRetries = ConfigUtilities.ParseInt(child.GetAttribute("MaxStorageBusyRetries"),
                                "Invalid integer value for the MaxStorageBusyRetries attribute on the SystemStore element");
                        }
                        if (child.HasAttribute("UseMockReminderTable"))
                        {
                            this.MockReminderTableTimeout = ConfigUtilities.ParseTimeSpan(child.GetAttribute("UseMockReminderTable"), "Invalid timeout value");
                            this.UseMockReminderTable = true;
                        }
                        break;
                    case "MultiClusterNetwork":
                        this.HasMultiClusterNetwork = true;
                        this.ClusterId = child.GetAttribute("ClusterId");

                        // we always trim cluster ids to avoid surprises when parsing comma-separated lists
                        if (this.ClusterId != null)
                            this.ClusterId = this.ClusterId.Trim(); 

                        if (string.IsNullOrEmpty(this.ClusterId))
                            throw new FormatException("MultiClusterNetwork.ClusterId cannot be blank");
                        if (this.ClusterId.Contains(","))
                            throw new FormatException("MultiClusterNetwork.ClusterId cannot contain commas: " + this.ClusterId);

                        if (child.HasAttribute("DefaultMultiCluster"))
                        {
                            var toparse = child.GetAttribute("DefaultMultiCluster").Trim();
                            if (string.IsNullOrEmpty(toparse))
                            {
                                this.DefaultMultiCluster = new List<string>(); // empty cluster
                            }
                            else
                            {
                                this.DefaultMultiCluster = toparse.Split(',').Select(id => id.Trim()).ToList();
                                foreach (var id in this.DefaultMultiCluster)
                                    if (string.IsNullOrEmpty(id))
                                        throw new FormatException("MultiClusterNetwork.DefaultMultiCluster cannot contain blank cluster ids: " + toparse);
                            }
                        }
                        if (child.HasAttribute("BackgroundGossipInterval"))
                        {
                            this.BackgroundGossipInterval = ConfigUtilities.ParseTimeSpan(child.GetAttribute("BackgroundGossipInterval"),
                                "Invalid time value for the BackgroundGossipInterval attribute on the MultiClusterNetwork element");
                        }
                        if (child.HasAttribute("UseGlobalSingleInstanceByDefault"))
                        {
                            this.UseGlobalSingleInstanceByDefault = ConfigUtilities.ParseBool(child.GetAttribute("UseGlobalSingleInstanceByDefault"),
                                "Invalid boolean for the UseGlobalSingleInstanceByDefault attribute on the MultiClusterNetwork element");
                        }
                        if (child.HasAttribute("GlobalSingleInstanceRetryInterval"))
                        {
                            this.GlobalSingleInstanceRetryInterval = ConfigUtilities.ParseTimeSpan(child.GetAttribute("GlobalSingleInstanceRetryInterval"),
                                "Invalid time value for the GlobalSingleInstanceRetryInterval attribute on the MultiClusterNetwork element");
                        }
                        if (child.HasAttribute("GlobalSingleInstanceNumberRetries"))
                        {
                            this.GlobalSingleInstanceNumberRetries = ConfigUtilities.ParseInt(child.GetAttribute("GlobalSingleInstanceNumberRetries"),
                                "Invalid value for the GlobalSingleInstanceRetryInterval attribute on the MultiClusterNetwork element");
                        }
                        if (child.HasAttribute("MaxMultiClusterGateways"))
                        {
                            this.MaxMultiClusterGateways = ConfigUtilities.ParseInt(child.GetAttribute("MaxMultiClusterGateways"),
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
                        this.GossipChannels = channels;
                        break;
                    case "SeedNode":
                        this.SeedNodes.Add(ConfigUtilities.ParseIPEndPoint(child, this.Subnet).GetResult());
                        break;

                    case "Messaging":
                        base.Load(child);
                        break;

                    case "Application":
                        this.Application.Load(child);
                        break;

                    case "PlacementStrategy":
                        if (child.HasAttribute("DefaultPlacementStrategy"))
                            this.DefaultPlacementStrategy = child.GetAttribute("DefaultPlacementStrategy");
                        if (child.HasAttribute("DeploymentLoadPublisherRefreshTime"))
                            this.DeploymentLoadPublisherRefreshTime = ConfigUtilities.ParseTimeSpan(child.GetAttribute("DeploymentLoadPublisherRefreshTime"),
                                "Invalid time span value for PlacementStrategy.DeploymentLoadPublisherRefreshTime");
                        if (child.HasAttribute("ActivationCountBasedPlacementChooseOutOf"))
                            this.ActivationCountBasedPlacementChooseOutOf = ConfigUtilities.ParseInt(child.GetAttribute("ActivationCountBasedPlacementChooseOutOf"),
                                "Invalid ActivationCountBasedPlacementChooseOutOf setting");
                        break;

                    case "Caching":
                        if (child.HasAttribute("CacheSize"))
                            this.CacheSize = ConfigUtilities.ParseInt(child.GetAttribute("CacheSize"),
                                "Invalid integer value for Caching.CacheSize");

                        if (child.HasAttribute("InitialTTL"))
                            this.InitialCacheTTL = ConfigUtilities.ParseTimeSpan(child.GetAttribute("InitialTTL"),
                                "Invalid time value for Caching.InitialTTL");

                        if (child.HasAttribute("MaximumTTL"))
                            this.MaximumCacheTTL = ConfigUtilities.ParseTimeSpan(child.GetAttribute("MaximumTTL"),
                                "Invalid time value for Caching.MaximumTTL");

                        if (child.HasAttribute("TTLExtensionFactor"))
                            this.CacheTTLExtensionFactor = ConfigUtilities.ParseDouble(child.GetAttribute("TTLExtensionFactor"),
                                "Invalid double value for Caching.TTLExtensionFactor");
                        if (this.CacheTTLExtensionFactor <= 1.0)
                        {
                            throw new FormatException("Caching.TTLExtensionFactor must be greater than 1.0");
                        }

                        if (child.HasAttribute("DirectoryCachingStrategy"))
                            this.DirectoryCachingStrategy = ConfigUtilities.ParseEnum<DirectoryCachingStrategyType>(child.GetAttribute("DirectoryCachingStrategy"),
                                "Invalid value for Caching.Strategy");

                        break;

                    case "Directory":
                        if (child.HasAttribute("DirectoryLazyDeregistrationDelay"))
                        {
                            this.DirectoryLazyDeregistrationDelay = ConfigUtilities.ParseTimeSpan(child.GetAttribute("DirectoryLazyDeregistrationDelay"),
                                "Invalid time span value for Directory.DirectoryLazyDeregistrationDelay");
                        }
                        if (child.HasAttribute("ClientRegistrationRefresh"))
                        {
                            this.ClientRegistrationRefresh = ConfigUtilities.ParseTimeSpan(child.GetAttribute("ClientRegistrationRefresh"),
                                "Invalid time span value for Directory.ClientRegistrationRefresh");
                        }
                        break;

                    default:
                        if (child.LocalName.Equals("GrainServices", StringComparison.Ordinal))
                        {
                            this.GrainServiceConfigurations = GrainServiceConfigurations.Load(child);
                        }

                        if (child.LocalName.EndsWith("Providers", StringComparison.Ordinal))
                        {
                            var providerCategory = ProviderCategoryConfiguration.Load(child);

                            if (this.ProviderConfigurations.ContainsKey(providerCategory.Name))
                            {
                                var existingCategory = this.ProviderConfigurations[providerCategory.Name];
                                existingCategory.Merge(providerCategory);
                            }
                            else
                            {
                                this.ProviderConfigurations.Add(providerCategory.Name, providerCategory);
                            }
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Registers a given type of <typeparamref name="T"/> where <typeparamref name="T"/> is stream provider
        /// </summary>
        /// <typeparam name="T">Non-abstract type which implements <see cref="Orleans.Streams.IStreamProvider"/> stream</typeparam>
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

            ProviderConfigurationUtility.RegisterProvider(this.ProviderConfigurations, ProviderCategoryConfiguration.STREAM_PROVIDER_CATEGORY_NAME, providerType.FullName, providerName, properties);
        }

        /// <summary>
        /// Registers a given stream provider.
        /// </summary>
        /// <param name="providerTypeFullName">Full name of the stream provider type</param>
        /// <param name="providerName">Name of the stream provider</param>
        /// <param name="properties">Properties that will be passed to the stream provider upon initialization </param>
        public void RegisterStreamProvider(string providerTypeFullName, string providerName, IDictionary<string, string> properties = null)
        {
            ProviderConfigurationUtility.RegisterProvider(this.ProviderConfigurations, ProviderCategoryConfiguration.STREAM_PROVIDER_CATEGORY_NAME, providerTypeFullName, providerName, properties);
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

            ProviderConfigurationUtility.RegisterProvider(this.ProviderConfigurations, ProviderCategoryConfiguration.STORAGE_PROVIDER_CATEGORY_NAME, providerTypeInfo.FullName, providerName, properties);
        }

        /// <summary>
        /// Registers a given storage provider.
        /// </summary>
        /// <param name="providerTypeFullName">Full name of the storage provider type</param>
        /// <param name="providerName">Name of the storage provider</param>
        /// <param name="properties">Properties that will be passed to the storage provider upon initialization </param>
        public void RegisterStorageProvider(string providerTypeFullName, string providerName, IDictionary<string, string> properties = null)
        {
            ProviderConfigurationUtility.RegisterProvider(this.ProviderConfigurations, ProviderCategoryConfiguration.STORAGE_PROVIDER_CATEGORY_NAME, providerTypeFullName, providerName, properties);
        }

        /// <summary>
        /// Registers a given log-consistency provider.
        /// </summary>
        /// <param name="providerTypeFullName">Full name of the log-consistency provider type</param>
        /// <param name="providerName">Name of the log-consistency provider</param>
        /// <param name="properties">Properties that will be passed to the log-consistency provider upon initialization </param>
        public void RegisterLogConsistencyProvider(string providerTypeFullName, string providerName, IDictionary<string, string> properties = null)
        {
            ProviderConfigurationUtility.RegisterProvider(this.ProviderConfigurations, ProviderCategoryConfiguration.LOG_CONSISTENCY_PROVIDER_CATEGORY_NAME, providerTypeFullName, providerName, properties);
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

            ProviderConfigurationUtility.RegisterProvider(this.ProviderConfigurations, ProviderCategoryConfiguration.LOG_CONSISTENCY_PROVIDER_CATEGORY_NAME, providerType.FullName, providerName, properties);
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
            return ProviderConfigurationUtility.TryGetProviderConfiguration(this.ProviderConfigurations, providerTypeFullName, providerName, out config);
        }

        /// <summary>
        /// Retrieves an enumeration of all currently configured provider configurations.
        /// </summary>
        /// <returns>An enumeration of all currently configured provider configurations.</returns>
        public IEnumerable<IProviderConfiguration> GetAllProviderConfigurations()
        {
            return ProviderConfigurationUtility.GetAllProviderConfigurations(this.ProviderConfigurations);
        }

        public void RegisterGrainService(string serviceName, string serviceType, IDictionary<string, string> properties = null)
        {
            GrainServiceConfigurationsUtility.RegisterGrainService(this.GrainServiceConfigurations, serviceName, serviceType, properties);
        }
    }
}
