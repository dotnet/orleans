/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Net;
using System.Xml;
using Orleans.AzureUtils;
﻿using Orleans.Providers;
using Orleans.Streams;
using Orleans.Storage;

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
        /// Service Id.
        /// </summary>
        public Guid ServiceId { get; set; }
        /// <summary>
        /// Deployment Id.
        /// </summary>
        public string DeploymentId { get; set; }
        /// <summary>
        /// Connection string for Azure Storage or SQL Server.
        /// </summary>
        public string DataConnectionString { get; set; }

        internal TimeSpan CollectionQuantum { get; set; }

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


        /// <summary>
        /// Determines if SqlServer should be used for storage of Membership and Reminders info.
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
        /// Determines if Azure Storage should be used for storage of Membership and Reminders info.
        /// True if either or both of LivenessType and ReminderServiceType are set to AzureTable, false otherwise.
        /// </summary>
        internal bool UseAzureSystemStore
        {
            get
            {
                return !String.IsNullOrWhiteSpace(DataConnectionString)
                    && !UseSqlSystemStore;
            }
        }

        internal bool RunsInAzure { get { return UseAzureSystemStore && !String.IsNullOrWhiteSpace(DeploymentId); } }

        private static readonly TimeSpan DEFAULT_LIVENESS_PROBE_TIMEOUT = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DEFAULT_LIVENESS_TABLE_REFRESH_TIMEOUT = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan DEFAULT_LIVENESS_DEATH_VOTE_EXPIRATION_TIMEOUT = TimeSpan.FromSeconds(120);
        private static readonly TimeSpan DEFAULT_LIVENESS_I_AM_ALIVE_TABLE_PUBLISH_TIMEOUT = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan DEFAULT_LIVENESS_MAX_JOIN_ATTEMPT_TIME = TimeSpan.FromMinutes(5); // 5 min
        private const int DEFAULT_LIVENESS_NUM_MISSED_PROBES_LIMIT = 3;
        private const int DEFAULT_LIVENESS_NUM_PROBED_SILOS = 3;
        private const int DEFAULT_LIVENESS_NUM_VOTES_FOR_DEATH_DECLARATION = 2;
        private const int DEFAULT_LIVENESS_NUM_TABLE_I_AM_ALIVE_LIMIT = 2;
        private const bool DEFAULT_LIVENESS_USE_LIVENESS_GOSSIP = true;
        private const int DEFAULT_LIVENESS_EXPECTED_CLUSTER_SIZE = 20;
        private const int DEFAULT_CACHE_SIZE = 1000000;
        private static readonly TimeSpan DEFAULT_INITIAL_CACHE_TTL = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DEFAULT_MAXIMUM_CACHE_TTL = TimeSpan.FromSeconds(240);
        private const double DEFAULT_TTL_EXTENSION_FACTOR = 2.0;
        private const DirectoryCachingStrategyType DEFAULT_DIRECTORY_CACHING_STRATEGY = DirectoryCachingStrategyType.Adaptive;
        internal static readonly TimeSpan DEFAULT_COLLECTION_QUANTUM = TimeSpan.FromMinutes(1);
        internal static readonly TimeSpan DEFAULT_COLLECTION_AGE_LIMIT = TimeSpan.FromHours(2);
        private static readonly TimeSpan DEFAULT_UNREGISTER_RACE_DELAY = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan DEFAULT_CLIENT_REGISTRATION_REFRESH = TimeSpan.FromMinutes(5);
        public const bool DEFAULT_PERFORM_DEADLOCK_DETECTION = false;
        public static readonly string DEFAULT_PLACEMENT_STRATEGY = typeof(RandomPlacement).Name;
        private static readonly TimeSpan DEFAULT_DEPLOYMENT_LOAD_PUBLISHER_REFRESH_TIME = TimeSpan.FromSeconds(1);
        private const int DEFAULT_ACTIVATION_COUNT_BASED_PLACEMENT_CHOOSE_OUT_OF = 2;

        private const bool DEFAULT_USE_VIRTUAL_RING_BUCKETS = true;
        private const int DEFAULT_NUM_VIRTUAL_RING_BUCKETS = 30;
        private static readonly TimeSpan DEFAULT_MOCK_REMINDER_TABLE_TIMEOUT = TimeSpan.FromMilliseconds(50);

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
            MaxJoinAttemptTime = DEFAULT_LIVENESS_MAX_JOIN_ATTEMPT_TIME;
            ExpectedClusterSizeConfigValue = new ConfigValue<int>(DEFAULT_LIVENESS_EXPECTED_CLUSTER_SIZE, true);
            ServiceId = Guid.Empty;
            DeploymentId = Environment.UserName;
            DataConnectionString = "";

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

            ProviderConfigurations = new Dictionary<string, ProviderCategoryConfiguration>();
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
            sb.AppendFormat("      IAmAliveTablePublishTimeout: {0}", IAmAliveTablePublishTimeout).AppendLine();
            sb.AppendFormat("      NumMissedTableIAmAliveLimit: {0}", NumMissedTableIAmAliveLimit).AppendLine();
            sb.AppendFormat("      MaxJoinAttemptTime: {0}", MaxJoinAttemptTime).AppendLine();
            sb.AppendFormat("      ExpectedClusterSize: {0}", ExpectedClusterSize).AppendLine();
            sb.AppendFormat("   SystemStore:").AppendLine();
            // Don't print connection credentials in log files, so pass it through redactment filter
            string connectionStringForLog = ConfigUtilities.RedactConnectionStringInfo(DataConnectionString);
            sb.AppendFormat("      ConnectionString: {0}", connectionStringForLog).AppendLine();
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
            var logger = TraceLogger.GetLogger("OrleansConfiguration", TraceLogger.LoggerType.Runtime);
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
                            if (!"None".Equals(sst, StringComparison.InvariantCultureIgnoreCase))
                            {
                                LivenessType = (LivenessProviderType)Enum.Parse(typeof(LivenessProviderType), sst);
                                SetReminderServiceType((ReminderServiceProviderType)Enum.Parse(typeof(ReminderServiceProviderType), sst));
                            }
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
                        if (child.HasAttribute("MaxStorageBusyRetries"))
                        {
                            int maxBusyRetries = ConfigUtilities.ParseInt(child.GetAttribute("MaxStorageBusyRetries"),
                                "Invalid integer value for the MaxStorageBusyRetries attribute on the SystemStore element");
                            AzureTableDefaultPolicies.MaxBusyRetries = maxBusyRetries;
                        }
                        if (child.HasAttribute("UseMockReminderTable"))
                        {
                            MockReminderTableTimeout = ConfigUtilities.ParseTimeSpan(child.GetAttribute("UseMockReminderTable"), "Invalid timeout value");
                            UseMockReminderTable = true;
                        }
                        break;

                    case "SeedNode":
                        SeedNodes.Add(ConfigUtilities.ParseIPEndPoint(child, Subnet));
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
            if (providerType.IsAbstract ||
                providerType.IsGenericType ||
                !typeof(IBootstrapProvider).IsAssignableFrom(providerType))
                throw new ArgumentException("Expected non-generic, non-abstract type which implements IBootstrapProvider interface", "typeof(T)");

            ProviderConfigurationUtility.RegisterProvider(ProviderConfigurations, ProviderCategoryConfiguration.BOOTSTRAP_PROVIDER_CATEGORY_NAME, providerType.FullName, providerName, properties);
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
            if (providerType.IsAbstract ||
                providerType.IsGenericType ||
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
            if (providerType.IsAbstract ||
                providerType.IsGenericType ||
                !typeof(IStorageProvider).IsAssignableFrom(providerType))
                throw new ArgumentException("Expected non-generic, non-abstract type which implements IStorageProvider interface", "typeof(T)");

            ProviderConfigurationUtility.RegisterProvider(ProviderConfigurations, ProviderCategoryConfiguration.STORAGE_PROVIDER_CATEGORY_NAME, providerType.FullName, providerName, properties);
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
    }
}
