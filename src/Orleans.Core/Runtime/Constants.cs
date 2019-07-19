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

        public static readonly GrainId DirectoryServiceId = GrainId.GetSystemTargetGrainId(10);
        public static readonly GrainId DirectoryCacheValidatorId = GrainId.GetSystemTargetGrainId(11);
        public static readonly GrainId SiloControlId = GrainId.GetSystemTargetGrainId(12);
        public static readonly GrainId ClientObserverRegistrarId = GrainId.GetSystemTargetGrainId(13);
        public static readonly GrainId CatalogId = GrainId.GetSystemTargetGrainId(14);
        public static readonly GrainId MembershipOracleId = GrainId.GetSystemTargetGrainId(15);
        public static readonly GrainId TypeManagerId = GrainId.GetSystemTargetGrainId(17);
        public static readonly GrainId FallbackSystemTargetId = GrainId.GetSystemTargetGrainId(19);
        public static readonly GrainId LifecycleSchedulingSystemTargetId = GrainId.GetSystemTargetGrainId(20);
        public static readonly GrainId DeploymentLoadPublisherSystemTargetId = GrainId.GetSystemTargetGrainId(22);
        public static readonly GrainId MultiClusterOracleId = GrainId.GetSystemTargetGrainId(23);
        public static readonly GrainId ClusterDirectoryServiceId = GrainId.GetSystemTargetGrainId(24);
        public static readonly GrainId StreamProviderManagerAgentSystemTargetId = GrainId.GetSystemTargetGrainId(25);
        public static readonly GrainId TestHooksSystemTargetId = GrainId.GetSystemTargetGrainId(26);
        public static readonly GrainId ProtocolGatewayId = GrainId.GetSystemTargetGrainId(27);
        public static readonly GrainId TransactionAgentSystemTargetId = GrainId.GetSystemTargetGrainId(28);
        public static readonly GrainId SystemMembershipTableId = GrainId.GetSystemTargetGrainId(29);
        public static readonly GrainId SiloDirectConnectionId = GrainId.GetSystemGrainId(new Guid("01111111-1111-1111-1111-111111111111"));

        public const int PULLING_AGENTS_MANAGER_SYSTEM_TARGET_TYPE_CODE = 254;
        public const int PULLING_AGENT_SYSTEM_TARGET_TYPE_CODE = 255;

        internal const long ReminderTableGrainId = 12345;

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

        private static readonly Dictionary<GrainId, string> singletonSystemTargetNames = new Dictionary<GrainId, string>
        {
            {DirectoryServiceId, "DirectoryService"},
            {DirectoryCacheValidatorId, "DirectoryCacheValidator"},
            {SiloControlId,"SiloControl"},
            {ClientObserverRegistrarId,"ClientObserverRegistrar"},
            {CatalogId,"Catalog"},
            {MembershipOracleId,"MembershipOracle"},
            {MultiClusterOracleId,"MultiClusterOracle"},
            {TypeManagerId,"TypeManagerId"},
            {ProtocolGatewayId,"ProtocolGateway"},
            {FallbackSystemTargetId, "FallbackSystemTarget"},
            {DeploymentLoadPublisherSystemTargetId, "DeploymentLoadPublisherSystemTarget"},
        };

        private static readonly Dictionary<int, string> nonSingletonSystemTargetNames = new Dictionary<int, string>
        {
            {PULLING_AGENT_SYSTEM_TARGET_TYPE_CODE, "PullingAgentSystemTarget"},
            {PULLING_AGENTS_MANAGER_SYSTEM_TARGET_TYPE_CODE, "PullingAgentsManagerSystemTarget"},
        };

        public static ushort DefaultInterfaceVersion = 1;

        public static string SystemTargetName(GrainId id)
        {
            string name;
            if (singletonSystemTargetNames.TryGetValue(id, out name)) return name;
            if (nonSingletonSystemTargetNames.TryGetValue(id.TypeCode, out name)) return name;
            return String.Empty;
        }

        public static bool IsSingletonSystemTarget(GrainId id)
        {
            return singletonSystemTargetNames.ContainsKey(id);
        }
    }
}
 
