using System;
using System.Text;

namespace Orleans.Runtime
{
    public static class GrainTypePrefix
    {
        public const string SystemPrefix = "sys.";
        public const string SystemTargetPrefix = SystemPrefix + "st.";
        public static readonly ReadOnlyMemory<byte> SystemTargetPrefixBytes = Encoding.UTF8.GetBytes(SystemTargetPrefix);

        public const string ClientPrefix = SystemPrefix + "client";
        public static readonly ReadOnlyMemory<byte> ClientPrefixBytes = Encoding.UTF8.GetBytes(ClientPrefix);

        public const string LegacyGrainPrefix = SystemPrefix + "grain.v1.";
        public static readonly ReadOnlyMemory<byte> LegacyGrainPrefixBytes = Encoding.UTF8.GetBytes(LegacyGrainPrefix);

        private static bool IsClient(this in GrainType type) => type.Value.Span.StartsWith(ClientPrefixBytes.Span);

        private static bool IsSystemTarget(this in GrainType type) => type.Value.Span.StartsWith(SystemTargetPrefixBytes.Span);

        private static bool IsLegacyGrain(this in GrainType type) => type.Value.Span.StartsWith(LegacyGrainPrefixBytes.Span);

        public static bool IsClient(this in GrainId id) => id.Type.IsClient() || LegacyGrainId.TryConvertFromGrainId(id, out var legacyId) && legacyId.IsClient;

        public static bool IsSystemTarget(this in GrainId id) => id.Type.IsSystemTarget() || LegacyGrainId.TryConvertFromGrainId(id, out var legacyId) && legacyId.IsSystemTarget;

        public static bool IsLegacyGrain(this in GrainId id) => id.Type.IsLegacyGrain()
            || (LegacyGrainId.TryConvertFromGrainId(id, out var legacyId) && legacyId.IsGrain);

        public static GrainId GetSystemTargetGrainId(GrainType kind, SiloAddress address)
        {
            return GrainId.Create(kind, address.ToParsableString());
        }
    }
}
