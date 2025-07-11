using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Orleans.GrainDirectory;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents an entry in a <see cref="IGrainDirectory"/>
    /// </summary>
    [GenerateSerializer, Immutable]
    public sealed class GrainAddress : IEquatable<GrainAddress>, ISpanFormattable
    {
        [Id(0)]
        private readonly GrainId _grainId;

        [Id(1)]
        private readonly ActivationId _activationId;

        /// <summary>
        /// Identifier of the Grain
        /// </summary>
        public GrainId GrainId { get => _grainId; init => _grainId = value; }

        /// <summary>
        /// Id of the specific Grain activation
        /// </summary>
        public ActivationId ActivationId { get => _activationId; init => _activationId = value; }

        /// <summary>
        /// Address of the silo where the grain activation lives
        /// </summary>
        [Id(2)]
        public SiloAddress? SiloAddress { get; init; }

        /// <summary>
        /// MembershipVersion at the time of registration
        /// </summary>
        [Id(3)]
        public MembershipVersion MembershipVersion { get; init; } = MembershipVersion.MinValue;

        [JsonIgnore]
        public bool IsComplete => !_grainId.IsDefault && !_activationId.IsDefault && SiloAddress != null;

        public override bool Equals(object? obj) => Equals(obj as GrainAddress);

        public bool Equals(GrainAddress? other)
        {
            if (ReferenceEquals(this, other)) return true;
            return MatchesGrainIdAndSilo(this, other)
                && _activationId.Equals(other._activationId);
        }

        /// <summary>
        /// Two grain addresses match if they have equal <see cref="SiloAddress"/> and <see cref="GrainId"/> values
        /// and either one has a default <see cref="ActivationId"/> value or both have equal <see cref="ActivationId"/> values.
        /// </summary>
        /// <param name="other"> The other <see cref="GrainAddress"/> to compare this one with.</param>
        /// <returns> Returns <c>true</c> if the two <see cref="GrainAddress"/> are considered to match.</returns>
        public bool Matches([NotNullWhen(true)] GrainAddress? other)
        {
            if (ReferenceEquals(this, other)) return true;
            return MatchesGrainIdAndSilo(this, other)
                && (_activationId.IsDefault || other._activationId.IsDefault || _activationId.Equals(other._activationId));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool MatchesGrainIdAndSilo([NotNullWhen(true)] GrainAddress? address, [NotNullWhen(true)] GrainAddress? other)
        {
            return other is not null
                && address is not null
                && address.GrainId.Equals(other.GrainId)
                && !(address.SiloAddress is null ^ other.SiloAddress is null)
                && (address.SiloAddress is null || address.SiloAddress.Equals(other.SiloAddress));
        }

        public override int GetHashCode() => HashCode.Combine(SiloAddress, _grainId, _activationId);

        public override string ToString() => $"[{nameof(GrainAddress)} GrainId {_grainId}, ActivationId: {_activationId}, SiloAddress: {SiloAddress}]";

        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
            => destination.TryWrite($"[{nameof(GrainAddress)} GrainId {_grainId}, ActivationId: {_activationId}, SiloAddress: {SiloAddress}]", out charsWritten);

        public string ToFullString() => $"[{nameof(GrainAddress)} GrainId {_grainId}, ActivationId: {_activationId}, SiloAddress: {SiloAddress}, MembershipVersion: {MembershipVersion}]";

        internal static GrainAddress NewActivationAddress(SiloAddress silo, GrainId grain) => GetAddress(silo, grain, ActivationId.NewId());

        internal static GrainAddress GetAddress(SiloAddress? silo, GrainId grain, ActivationId activation)
        {
            // Silo part is not mandatory
            if (grain.IsDefault) throw new ArgumentNullException(nameof(grain));

            return new GrainAddress
            {
                GrainId = grain,
                ActivationId = activation,
                SiloAddress = silo,
            };
        }
    }
}
