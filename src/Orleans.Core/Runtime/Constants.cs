using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    internal class Constants
    {
        // This needs to be first, as GrainId static initializers reference it. Otherwise, GrainId actually see a uninitialized (ie Zero) value for that "constant"!
        public static readonly TimeSpan INFINITE_TIMESPAN = TimeSpan.FromMilliseconds(-1);

        // We assume that clock skew between silos and between clients and silos is always less than 1 second
        public static readonly TimeSpan MAXIMUM_CLOCK_SKEW = TimeSpan.FromSeconds(1);

        public const string DATA_CONNECTION_STRING_NAME = "DataConnectionString";
        public const string ADO_INVARIANT_NAME = "AdoInvariant";
        public const string DATA_CONNECTION_FOR_REMINDERS_STRING_NAME = "DataConnectionStringForReminders";
        public const string ADO_INVARIANT_FOR_REMINDERS_NAME = "AdoInvariantForReminders";

        public const string ORLEANS_CLUSTERING_AZURESTORAGE = "Orleans.Clustering.AzureStorage";
        public const string ORLEANS_REMINDERS_AZURESTORAGE = "Orleans.Reminders.AzureStorage";

        public const string ORLEANS_CLUSTERING_ADONET = "Orleans.Clustering.AdoNet";
        public const string ORLEANS_REMINDERS_ADONET = "Orleans.Reminders.AdoNet";

        public const string INVARIANT_NAME_SQL_SERVER = "System.Data.SqlClient";

        public const string ORLEANS_CLUSTERING_ZOOKEEPER = "Orleans.Clustering.ZooKeeper";
        public const string TroubleshootingHelpLink = "https://aka.ms/orleans-troubleshooting";

        public static readonly GrainType DirectoryServiceType = SystemTargetGrainId.CreateGrainType("dir.mem");
        public static readonly GrainType DirectoryCacheValidatorType = SystemTargetGrainId.CreateGrainType("dir.cache-validator");
        public static readonly GrainType SiloControlType = SystemTargetGrainId.CreateGrainType("silo-control");
        public static readonly GrainType ClientObserverRegistrarType = SystemTargetGrainId.CreateGrainType("observer.registrar");
        public static readonly GrainType CatalogType = SystemTargetGrainId.CreateGrainType("catalog");
        public static readonly GrainType MembershipOracleType = SystemTargetGrainId.CreateGrainType("clustering.oracle");
        public static readonly GrainType TypeManagerType = SystemTargetGrainId.CreateGrainType("type-manager");
        public static readonly GrainType FallbackSystemTargetType = SystemTargetGrainId.CreateGrainType("fallback");
        public static readonly GrainType LifecycleSchedulingSystemTargetType = SystemTargetGrainId.CreateGrainType("lifecycle");
        public static readonly GrainType DeploymentLoadPublisherSystemTargetType = SystemTargetGrainId.CreateGrainType("load-publisher");
        public static readonly GrainType MultiClusterOracleType = SystemTargetGrainId.CreateGrainType("multicluster-oracle");
        public static readonly GrainType ClusterDirectoryServiceType = SystemTargetGrainId.CreateGrainType("multicluster-directory");
        public static readonly GrainType StreamProviderManagerAgentSystemTargetType = SystemTargetGrainId.CreateGrainType("streams.provider-manager");
        public static readonly GrainType TestHooksSystemTargetType = SystemTargetGrainId.CreateGrainType("test.hooks");
        public static readonly GrainType ProtocolGatewayType = SystemTargetGrainId.CreateGrainType("multicluster.protocol-gw");
        public static readonly GrainType TransactionAgentSystemTargetType = SystemTargetGrainId.CreateGrainType("txn.agent");
        public static readonly GrainType SystemMembershipTableType = SystemTargetGrainId.CreateGrainType("clustering.dev");
        public static readonly GrainType StreamPullingAgentManagerType = SystemTargetGrainId.CreateGrainType("stream-agent-mgr");
        public static readonly GrainType StreamPullingAgentType = SystemTargetGrainId.CreateGrainType("stream-agent");
        public static readonly GrainType ManifestProviderType = SystemTargetGrainId.CreateGrainType("manifest");

        public static readonly GrainId SiloDirectConnectionId = GrainId.Create(
            GrainType.Create(GrainTypePrefix.SystemPrefix + "silo"),
            IdSpan.Create("01111111-1111-1111-1111-111111111111"));

        /// <summary>
        /// Minimum period for registering a reminder ... we want to enforce a lower bound
        /// </summary>
        public static readonly TimeSpan MinReminderPeriod = TimeSpan.FromMinutes(1); // increase this period, reminders are supposed to be less frequent ... we use 2 seconds just to reduce the running time of the unit tests
        /// <summary>
        /// Refresh local reminder list to reflect the global reminder table every 'REFRESH_REMINDER_LIST' period
        /// </summary>
        public static readonly TimeSpan RefreshReminderList = TimeSpan.FromMinutes(5);

        public const int LARGE_OBJECT_HEAP_THRESHOLD = 85000;

        public const int DEFAULT_LOGGER_BULK_MESSAGE_LIMIT = 5;

        public static readonly TimeSpan DEFAULT_CLIENT_DROP_TIMEOUT = TimeSpan.FromMinutes(1);

        private static readonly Dictionary<GrainType, string> singletonSystemTargetNames = new Dictionary<GrainType, string>
        {
            {DirectoryServiceType, "DirectoryService"},
            {DirectoryCacheValidatorType, "DirectoryCacheValidator"},
            {SiloControlType,"SiloControl"},
            {ClientObserverRegistrarType,"ClientObserverRegistrar"},
            {CatalogType,"Catalog"},
            {MembershipOracleType,"MembershipOracle"},
            {MultiClusterOracleType,"MultiClusterOracle"},
            {TypeManagerType,"TypeManagerId"},
            {ProtocolGatewayType,"ProtocolGateway"},
            {FallbackSystemTargetType, "FallbackSystemTarget"},
            {DeploymentLoadPublisherSystemTargetType, "DeploymentLoadPublisherSystemTarget"},
            {StreamPullingAgentType, "PullingAgentSystemTarget"},
            {StreamPullingAgentManagerType, "PullingAgentsManagerSystemTarget"},
        };

        public static ushort DefaultInterfaceVersion = 1;

        public static string SystemTargetName(GrainType id)
        {
            if (singletonSystemTargetNames.TryGetValue(id, out var name))
            {
                return name;
            }

            return id.ToStringUtf8();
        }

        public static bool IsSingletonSystemTarget(GrainType id)
        {
            return singletonSystemTargetNames.ContainsKey(id);
        }
    }
}
 
