using System;
using System.Text;

namespace Orleans.Runtime
{
    /// <summary>
    /// Prefixes and corresponding helper methods for <see cref="GrainType"/>.
    /// </summary>
    public static class GrainTypePrefix
    {
        /// <summary>
        /// The prefix for system types.
        /// </summary>
        public const string SystemPrefix = "sys.";

        /// <summary>
        /// The prefix for system targets.
        /// </summary>
        public const string SystemTargetPrefix = SystemPrefix + "svc.";

        /// <summary>
        /// A span representation of <see cref="SystemTargetPrefix" />.
        /// </summary>
        public static readonly ReadOnlyMemory<byte> SystemTargetPrefixBytes = Encoding.UTF8.GetBytes(SystemTargetPrefix);

        /// <summary>
        /// The prefix for grain service types.
        /// </summary>
        public const string GrainServicePrefix = SystemTargetPrefix + "user.";

        /// <summary>
        /// The prefix for clients.
        /// </summary>
        public const string ClientPrefix = SystemPrefix + "client";

        /// <summary>
        /// A span representation of <see cref="ClientPrefix" />.
        /// </summary>
        public static readonly ReadOnlyMemory<byte> ClientPrefixBytes = Encoding.UTF8.GetBytes(ClientPrefix);
        public static readonly GrainType ClientGrainType = GrainType.Create(ClientPrefix);

        /// <summary>
        /// The prefix for legacy grains.
        /// </summary>
        public const string LegacyGrainPrefix = SystemPrefix + "grain.v1.";

        /// <summary>
        /// A span representation of <see cref="LegacyGrainPrefixBytes" />.
        /// </summary>
        public static readonly ReadOnlyMemory<byte> LegacyGrainPrefixBytes = Encoding.UTF8.GetBytes(LegacyGrainPrefix);

        /// <summary>
        /// Returns <see langword="true"/> if the type is a client, <see langword="false"/> if not.
        /// </summary>
        public static bool IsClient(this in GrainType type) => type.AsSpan().StartsWith(ClientPrefixBytes.Span);

        /// <summary>
        /// Returns <see langword="true"/> if the type is a system target, <see langword="false"/> if not.
        /// </summary>
        public static bool IsSystemTarget(this in GrainType type) => type.AsSpan().StartsWith(SystemTargetPrefixBytes.Span);

        /// <summary>
        /// Returns <see langword="true"/> if the type is a legacy grain, <see langword="false"/> if not.
        /// </summary>
        private static bool IsLegacyGrain(this in GrainType type) => type.AsSpan().StartsWith(LegacyGrainPrefixBytes.Span);

        /// <summary>
        /// Returns <see langword="true"/> if the type is a client, <see langword="false"/> if not.
        /// </summary>
        public static bool IsClient(this in GrainId id) => id.Type.IsClient();

        /// <summary>
        /// Returns <see langword="true"/> if the type is a system target, <see langword="false"/> if not.
        /// </summary>
        public static bool IsSystemTarget(this in GrainId id) => id.Type.IsSystemTarget();

        /// <summary>
        /// Returns <see langword="true"/> if the type is a legacy grain, <see langword="false"/> if not.
        /// </summary>
        public static bool IsLegacyGrain(this in GrainId id) => id.Type.IsLegacyGrain()
            || LegacyGrainId.TryConvertFromGrainId(id, out var legacyId) && legacyId.IsGrain;
    }
}
