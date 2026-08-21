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
        /// The prefix for grain references whose implementation type is pending resolution.
        /// </summary>
        internal const string StubGrainPrefix = SystemPrefix + "grain.stub.";

        /// <summary>
        /// A span representation of <see cref="StubGrainPrefix"/>.
        /// </summary>
        internal static readonly ReadOnlyMemory<byte> StubGrainPrefixBytes = Encoding.UTF8.GetBytes(StubGrainPrefix);

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

        /// <summary>
        /// Returns <see langword="true"/> if the grain type is pending implementation resolution.
        /// </summary>
        /// <param name="type">The grain type.</param>
        /// <returns><see langword="true"/> if the grain type is pending implementation resolution; otherwise, <see langword="false"/>.</returns>
        internal static bool IsStubGrain(this in GrainType type) => type.AsSpan().StartsWith(StubGrainPrefixBytes.Span);

        /// <summary>
        /// Creates a grain type which records that implementation resolution is pending.
        /// </summary>
        /// <param name="interfaceType">The grain interface type.</param>
        /// <param name="grainClassPrefix">The implementation class prefix.</param>
        /// <returns>The grain type.</returns>
        internal static GrainType CreateStubGrainType(GrainInterfaceType interfaceType, string? grainClassPrefix)
        {
            var encodedInterfaceType = EncodeBase64Url(interfaceType.ToString());
            var encodedClassPrefix = EncodeBase64Url(grainClassPrefix ?? string.Empty);
            return GrainType.Create(string.Concat(StubGrainPrefix, encodedInterfaceType, ".", encodedClassPrefix));
        }

        /// <summary>
        /// Extracts resolution data from a grain type which is pending implementation resolution.
        /// </summary>
        /// <param name="grainType">The grain type.</param>
        /// <param name="interfaceType">The interface type used to select the grain implementation.</param>
        /// <param name="grainClassPrefix">The implementation class prefix.</param>
        /// <returns><see langword="true"/> if the grain type is pending implementation resolution; otherwise, <see langword="false"/>.</returns>
        internal static bool TryGetStubGrainType(GrainType grainType, out GrainInterfaceType interfaceType, out string grainClassPrefix)
        {
            if (!IsStubGrain(grainType))
            {
                interfaceType = default;
                grainClassPrefix = string.Empty;
                return false;
            }

            var encodedType = Encoding.UTF8.GetString(grainType.AsSpan().Slice(StubGrainPrefixBytes.Length));
            var separatorIndex = encodedType.IndexOf('.');
            if (separatorIndex < 0)
            {
                interfaceType = default;
                grainClassPrefix = string.Empty;
                return false;
            }

            try
            {
                interfaceType = GrainInterfaceType.Create(DecodeBase64Url(encodedType[..separatorIndex]));
                grainClassPrefix = DecodeBase64Url(encodedType[(separatorIndex + 1)..]);
                return true;
            }
            catch (FormatException)
            {
                interfaceType = default;
                grainClassPrefix = string.Empty;
                return false;
            }
        }

        private static string EncodeBase64Url(string value)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static string DecodeBase64Url(string value)
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 = (base64.Length % 4) switch
            {
                0 => base64,
                2 => string.Concat(base64, "=="),
                3 => string.Concat(base64, "="),
                _ => throw new FormatException("Invalid Base64Url value."),
            };

            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
    }
}
