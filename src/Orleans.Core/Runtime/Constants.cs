using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    internal static class Constants
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
        public static readonly GrainType ClientDirectoryType = SystemTargetGrainId.CreateGrainType("dir.client");
        public static readonly GrainType SiloControlType = SystemTargetGrainId.CreateGrainType("silo-control");
        public static readonly GrainType CatalogType = SystemTargetGrainId.CreateGrainType("catalog");
        public static readonly GrainType MembershipServiceType = SystemTargetGrainId.CreateGrainType("clustering");
        public static readonly GrainType SystemMembershipTableType = SystemTargetGrainId.CreateGrainType("clustering.dev");
        public static readonly GrainType FallbackSystemTargetType = SystemTargetGrainId.CreateGrainType("fallback");
        public static readonly GrainType LifecycleSchedulingSystemTargetType = SystemTargetGrainId.CreateGrainType("lifecycle");
        public static readonly GrainType DeploymentLoadPublisherSystemTargetType = SystemTargetGrainId.CreateGrainType("load-publisher");
        public static readonly GrainType TestHooksSystemTargetType = SystemTargetGrainId.CreateGrainType("test.hooks");
        public static readonly GrainType TransactionAgentSystemTargetType = SystemTargetGrainId.CreateGrainType("txn.agent");
        public static readonly GrainType StreamProviderManagerAgentSystemTargetType = SystemTargetGrainId.CreateGrainType("stream.provider-manager");
        public static readonly GrainType StreamPullingAgentManagerType = SystemTargetGrainId.CreateGrainType("stream.agent-mgr");
        public static readonly GrainType StreamPullingAgentType = SystemTargetGrainId.CreateGrainType("stream.agent");
        public static readonly GrainType ManifestProviderType = SystemTargetGrainId.CreateGrainType("manifest");
        public static readonly GrainType ActivationMigratorType = SystemTargetGrainId.CreateGrainType("migrator");

        public static readonly GrainId SiloDirectConnectionId = GrainId.Create(
            GrainType.Create(GrainTypePrefix.SystemPrefix + "silo"),
            IdSpan.Create("01111111-1111-1111-1111-111111111111"));

        public const int LARGE_OBJECT_HEAP_THRESHOLD = 85000;

        public const int DEFAULT_LOGGER_BULK_MESSAGE_LIMIT = 5;

        public static readonly TimeSpan DEFAULT_CLIENT_DROP_TIMEOUT = TimeSpan.FromMinutes(1);

        private static readonly Dictionary<GrainType, string> singletonSystemTargetNames = new Dictionary<GrainType, string>
        {
            {DirectoryServiceType, "DirectoryService"},
            {DirectoryCacheValidatorType, "DirectoryCacheValidator"},
            {SiloControlType, "SiloControl"},
            {ClientDirectoryType, "ClientDirectory"},
            {CatalogType,"Catalog"},
            {MembershipServiceType,"MembershipService"},
            {FallbackSystemTargetType, "FallbackSystemTarget"},
            {LifecycleSchedulingSystemTargetType, "LifecycleSchedulingSystemTarget"},
            {DeploymentLoadPublisherSystemTargetType, "DeploymentLoadPublisherSystemTarget"},
            {StreamProviderManagerAgentSystemTargetType,"StreamProviderManagerAgent"},
            {TestHooksSystemTargetType,"TestHooksSystemTargetType"},
            {TransactionAgentSystemTargetType,"TransactionAgentSystemTarget"},
            {SystemMembershipTableType,"SystemMembershipTable"},
            {StreamPullingAgentManagerType, "PullingAgentsManagerSystemTarget"},
            {StreamPullingAgentType, "PullingAgentSystemTarget"},
            {ManifestProviderType, "ManifestProvider"},
        };

        public static ushort DefaultInterfaceVersion = 1;

        public static string SystemTargetName(GrainType id) => singletonSystemTargetNames.TryGetValue(id, out var name) ? name : id.ToString();

        public static bool IsSingletonSystemTarget(GrainType id)
        {
            return singletonSystemTargetNames.ContainsKey(id);
        }
    }
}
 
