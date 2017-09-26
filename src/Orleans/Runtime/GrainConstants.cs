using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    internal class GrainConstants
    {
        public const int PULLING_AGENTS_MANAGER_SYSTEM_TARGET_TYPE_CODE = 254;
        public const int PULLING_AGENT_SYSTEM_TARGET_TYPE_CODE = 255;
        internal const long ReminderTableGrainId = 12345;
        public static readonly GrainId DirectoryServiceId = GrainId.GetSystemTargetGrainId(10);
        public static readonly GrainId DirectoryCacheValidatorId = GrainId.GetSystemTargetGrainId(11);
        public static readonly GrainId SiloControlId = GrainId.GetSystemTargetGrainId(12);
        public static readonly GrainId ClientObserverRegistrarId = GrainId.GetSystemTargetGrainId(13);
        public static readonly GrainId CatalogId = GrainId.GetSystemTargetGrainId(14);
        public static readonly GrainId MembershipOracleId = GrainId.GetSystemTargetGrainId(15);
        public static readonly GrainId TypeManagerId = GrainId.GetSystemTargetGrainId(17);
        public static readonly GrainId ProviderManagerSystemTargetId = GrainId.GetSystemTargetGrainId(19);
        public static readonly GrainId DeploymentLoadPublisherSystemTargetId = GrainId.GetSystemTargetGrainId(22);
        public static readonly GrainId MultiClusterOracleId = GrainId.GetSystemTargetGrainId(23);
        public static readonly GrainId ClusterDirectoryServiceId = GrainId.GetSystemTargetGrainId(24);
        public static readonly GrainId StreamProviderManagerAgentSystemTargetId = GrainId.GetSystemTargetGrainId(25);
        public static readonly GrainId TestHooksSystemTargetId = GrainId.GetSystemTargetGrainId(26);
        public static readonly GrainId ProtocolGatewayId = GrainId.GetSystemTargetGrainId(27);
        public static readonly GrainId TransactionAgentSystemTargetId = GrainId.GetSystemTargetGrainId(28);
        public static readonly GrainId SystemMembershipTableId = GrainId.GetSystemGrainId(new Guid("01145FEC-C21E-11E0-9105-D0FB4724019B"));
        public static readonly GrainId SiloDirectConnectionId = GrainId.GetSystemGrainId(new Guid("01111111-1111-1111-1111-111111111111"));

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
            {ProviderManagerSystemTargetId, "ProviderManagerSystemTarget"},
            {DeploymentLoadPublisherSystemTargetId, "DeploymentLoadPublisherSystemTarget"},
        };

        private static readonly Dictionary<int, string> nonSingletonSystemTargetNames = new Dictionary<int, string>
        {
            {PULLING_AGENT_SYSTEM_TARGET_TYPE_CODE, "PullingAgentSystemTarget"},
            {PULLING_AGENTS_MANAGER_SYSTEM_TARGET_TYPE_CODE, "PullingAgentsManagerSystemTarget"},
        };

        private static readonly Dictionary<GrainId, string> systemGrainNames = new Dictionary<GrainId, string>
        {
            {SystemMembershipTableId, "MembershipTableGrain"},
            {SiloDirectConnectionId, "SiloDirectConnectionId"}
        };

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

        public static bool TryGetSystemGrainName(GrainId id, out string name)
        {
            return systemGrainNames.TryGetValue(id, out name);
        }

        public static bool IsSystemGrain(GrainId grain)
        {
            return systemGrainNames.ContainsKey(grain);
        }
    }
}