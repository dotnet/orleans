using System;
using System.Text;

namespace Orleans.Runtime
{
    public static class GrainTypePrefix
    {
        public const string SystemPrefix = "sys.";
        public const string SystemTargetPrefix = SystemPrefix + "svc.";
        public static readonly ReadOnlyMemory<byte> SystemTargetPrefixBytes = Encoding.UTF8.GetBytes(SystemTargetPrefix);

        public const string GrainServicePrefix = SystemTargetPrefix + "user.";
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

        public static GrainId GetSystemTargetGrainId(GrainType kind, SiloAddress address, string extraIdentifier)
        {
            if (extraIdentifier is string)
            {
                return GrainId.Create(kind, address.ToParsableString() + "+" + extraIdentifier);
            }

            return GetSystemTargetGrainId(kind, address);
        }

        public static GrainId ReplaceSystemTargetSilo(GrainId grainId, SiloAddress address)
        {
            string extraIdentifier = null;
            var key = grainId.Key.ToStringUtf8();
            if (key.IndexOf('+') is int index && index >= 0)
            {
                extraIdentifier = key.Substring(index + 1);
            }
            return GetSystemTargetGrainId(grainId.Type, address, extraIdentifier);
        }

        public static SiloAddress GetSystemTargetSilo(GrainId id)
        {
            var key = id.Key.ToStringUtf8();
            if (key.IndexOf('+') is int index && index >= 0)
            {
                key = key.Substring(0, index);
            }

            return SiloAddress.FromParsableString(key);
        }

        public static GrainId GetGrainServiceGrainId(int typeCode, string grainSystemId, SiloAddress address)
        {
            var grainType = GrainType.Create($"{GrainTypePrefix.GrainServicePrefix}{typeCode:X8}{grainSystemId}");
            return GrainId.Create(grainType, address.ToParsableString());
        }

        public static GrainType GetSystemTargetType(string name) => GrainType.Create($"{GrainTypePrefix.SystemTargetPrefix}{name}");
    }
}
