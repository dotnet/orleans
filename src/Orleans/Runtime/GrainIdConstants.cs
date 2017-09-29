using System;

namespace Orleans.Runtime
{
    internal class GrainIdConstants
    {
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
    }
}