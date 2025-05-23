using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    internal static class Constants
    {
        public const string TroubleshootingHelpLink = "https://aka.ms/orleans-troubleshooting";

        public static readonly GrainType DirectoryServiceType = SystemTargetGrainId.CreateGrainType("dir.mem");
        public static readonly GrainType DirectoryCacheValidatorType = SystemTargetGrainId.CreateGrainType("dir.cache-validator");
        public static readonly GrainType ClientDirectoryType = SystemTargetGrainId.CreateGrainType("dir.client");
        public static readonly GrainType SiloControlType = SystemTargetGrainId.CreateGrainType("silo-control");
        public static readonly GrainType SiloMetadataType = SystemTargetGrainId.CreateGrainType("silo-metadata");
        public static readonly GrainType CatalogType = SystemTargetGrainId.CreateGrainType("catalog");
        public static readonly GrainType MembershipServiceType = SystemTargetGrainId.CreateGrainType("clustering");
        public static readonly GrainType SystemMembershipTableType = SystemTargetGrainId.CreateGrainType("clustering.dev");
        public static readonly GrainType LifecycleSchedulingSystemTargetType = SystemTargetGrainId.CreateGrainType("lifecycle");
        public static readonly GrainType DeploymentLoadPublisherSystemTargetType = SystemTargetGrainId.CreateGrainType("load-publisher");
        public static readonly GrainType TestHooksSystemTargetType = SystemTargetGrainId.CreateGrainType("test.hooks");
        public static readonly GrainType TransactionAgentSystemTargetType = SystemTargetGrainId.CreateGrainType("txn.agent");
        public static readonly GrainType StreamProviderManagerAgentSystemTargetType = SystemTargetGrainId.CreateGrainType("stream.provider-manager");
        public static readonly GrainType StreamPullingAgentManagerType = SystemTargetGrainId.CreateGrainType("stream.agent-mgr");
        public static readonly GrainType StreamPullingAgentType = SystemTargetGrainId.CreateGrainType("stream.agent");
        public static readonly GrainType ManifestProviderType = SystemTargetGrainId.CreateGrainType("manifest");
        public static readonly GrainType ActivationMigratorType = SystemTargetGrainId.CreateGrainType("migrator");
        public static readonly GrainType CancellationManagerType = SystemTargetGrainId.CreateGrainType("canceler");
        public static readonly GrainType ActivationRepartitionerType = SystemTargetGrainId.CreateGrainType("repartitioner");
        public static readonly GrainType ActivationRebalancerMonitorType = SystemTargetGrainId.CreateGrainType("rebalancer-monitor");
        public static readonly GrainType GrainDirectoryPartitionType = SystemTargetGrainId.CreateGrainType("dir.grain.part");
        public static readonly GrainType GrainDirectoryType = SystemTargetGrainId.CreateGrainType("dir.grain");

        public static readonly GrainId SiloDirectConnectionId = GrainId.Create(
            GrainType.Create(GrainTypePrefix.SystemPrefix + "silo"),
            IdSpan.Create("01111111-1111-1111-1111-111111111111"));

        public static readonly TimeSpan DEFAULT_CLIENT_DROP_TIMEOUT = TimeSpan.FromMinutes(1);

        private static readonly FrozenDictionary<GrainType, string> SingletonSystemTargetNames = new Dictionary<GrainType, string>
        {
            {DirectoryServiceType, "DirectoryService"},
            {DirectoryCacheValidatorType, "DirectoryCacheValidator"},
            {SiloControlType, "SiloControl"},
            {SiloMetadataType, "SiloMetadata"},
            {ClientDirectoryType, "ClientDirectory"},
            {CatalogType,"Catalog"},
            {MembershipServiceType,"MembershipService"},
            {LifecycleSchedulingSystemTargetType, "LifecycleSchedulingSystemTarget"},
            {DeploymentLoadPublisherSystemTargetType, "DeploymentLoadPublisherSystemTarget"},
            {StreamProviderManagerAgentSystemTargetType,"StreamProviderManagerAgent"},
            {TestHooksSystemTargetType,"TestHooksSystemTargetType"},
            {TransactionAgentSystemTargetType,"TransactionAgentSystemTarget"},
            {SystemMembershipTableType,"SystemMembershipTable"},
            {StreamPullingAgentManagerType, "PullingAgentsManagerSystemTarget"},
            {StreamPullingAgentType, "PullingAgentSystemTarget"},
            {ManifestProviderType, "ManifestProvider"},
            {ActivationMigratorType, "ActivationMigrator"},
            {ActivationRepartitionerType, "ActivationRepartitioner"},
            {ActivationRebalancerMonitorType, "ActivationRebalancerMonitor"},
            {GrainDirectoryType, "GrainDirectory"},
        }.ToFrozenDictionary();

        public static string SystemTargetName(GrainType id) => SingletonSystemTargetNames.TryGetValue(id, out var name) ? name : id.ToString();
        public static bool IsSingletonSystemTarget(GrainType id) => SingletonSystemTargetNames.ContainsKey(id);
    }
}

