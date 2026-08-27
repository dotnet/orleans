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
        /// A span representation of <see cref="ClientPrefix" />.
        /// </summary>
        public static readonly ReadOnlyMemory<byte> GrainServicePrefixBytes = Encoding.UTF8.GetBytes(GrainServicePrefix);

        /// <summary>
        /// The prefix for clients.
        /// </summary>
        public const string ClientPrefix = SystemPrefix + "client";

        /// <summary>
        /// A span representation of <see cref="ClientPrefix" />.
        /// </summary>
        public static readonly ReadOnlyMemory<byte> ClientPrefixBytes = Encoding.UTF8.GetBytes(ClientPrefix);

        /// <summary>
        /// The prefix used to represent a grain client.
        /// </summary>
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
        /// <param name="type">The grain type.</param>
        /// <returns><see langword="true"/> if the type is a client, <see langword="false"/> if not.</returns>
        public static bool IsClient(this in GrainType type) => type.AsSpan().StartsWith(ClientPrefixBytes.Span);

        /// <summary>
        /// Returns <see langword="true"/> if the type is a system target, <see langword="false"/> if not.
        /// </summary>
        /// <param name="type">The grain type.</param>
        /// <returns><see langword="true"/> if the type is a system target, <see langword="false"/> if not.</returns>
        public static bool IsSystemTarget(this in GrainType type) => type.AsSpan().StartsWith(SystemTargetPrefixBytes.Span);

        /// <summary>
        /// Returns <see langword="true"/> if the type is a legacy grain, <see langword="false"/> if not.
        /// </summary>
        /// <param name="type">The grain type.</param>
        /// <returns><see langword="true"/> if the type is a legacy grain, <see langword="false"/> if not.</returns>
        public static bool IsLegacyGrain(this in GrainType type) => type.AsSpan().StartsWith(LegacyGrainPrefixBytes.Span);

        /// <summary>
        /// Returns <see langword="true"/> if the type is a grain service, <see langword="false"/> if not.
        /// </summary>
        /// <param name="type">The grain type.</param>
        /// <returns><see langword="true"/> if the type is a grain service, <see langword="false"/> if not.</returns>
        public static bool IsGrainService(this in GrainType type) => type.AsSpan().StartsWith(GrainServicePrefixBytes.Span);

        /// <summary>
        /// Returns <see langword="true"/> if the id represents a client, <see langword="false"/> if not.
        /// </summary>
        /// <param name="id">The grain id.</param>
        /// <returns><see langword="true"/> if the type is a client, <see langword="false"/> if not.</returns>
        public static bool IsClient(this in GrainId id) => id.Type.IsClient();

        /// <summary>
        /// Returns <see langword="true"/> if the id represents a system target, <see langword="false"/> if not.
        /// </summary>
        /// <param name="id">The grain id.</param>
        /// <returns><see langword="true"/> if the type is a system target, <see langword="false"/> if not.</returns>
        public static bool IsSystemTarget(this in GrainId id) => id.Type.IsSystemTarget();
    }
}
