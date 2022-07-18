using System;
using System.Text.Json.Serialization;
using Orleans.GrainDirectory;

#nullable enable
namespace Orleans.Runtime
{
    /// <summary>
    /// Represents an entry in a <see cref="IGrainDirectory"/>
    /// </summary>
    [GenerateSerializer]
    public sealed class GrainAddress : IEquatable<GrainAddress>, ISpanFormattable
    {
        /// <summary>
        /// Identifier of the Grain
        /// </summary>
        [Id(0)]
        public GrainId GrainId { get; set; }

        /// <summary>
        /// Id of the specific Grain activation
        /// </summary>
        [Id(1)]
        public ActivationId ActivationId { get; set; }

        /// <summary>
        /// Address of the silo where the grain activation lives
        /// </summary>
        [Id(2)]
        public SiloAddress? SiloAddress { get; set; }

        /// <summary>
        /// MembershipVersion at the time of registration
        /// </summary>
        [Id(3)]
        public MembershipVersion MembershipVersion { get; set; } = MembershipVersion.MinValue;

        [JsonIgnore]
        public bool IsComplete => !GrainId.IsDefault && !ActivationId.IsDefault && SiloAddress != null;

        public override bool Equals(object? obj) => Equals(obj as GrainAddress);

        public bool Equals(GrainAddress? other)
        {
            return other != null && (SiloAddress?.Equals(other.SiloAddress) ?? other.SiloAddress is null)
                && GrainId == other.GrainId && ActivationId == other.ActivationId && MembershipVersion == other.MembershipVersion;
        }

        /// <summary>
        /// Two grain addresses match if they have equal <see cref="SiloAddress"/> and <see cref="GrainId"/> values
        /// and either one has a default <see cref="ActivationId"/> value or both have equal <see cref="ActivationId"/> values.
        /// </summary>
        /// <param name="other"> The other <see cref="GrainAddress"/> to compare this one with.</param>
        /// <returns> Returns <c>true</c> if the two <see cref="GrainAddress"/> are considered to match.</returns>
        public bool Matches(GrainAddress other)
        {
            return other is not null && GrainId == other.GrainId && (SiloAddress?.Equals(other.SiloAddress) ?? other.SiloAddress is null)
                && (ActivationId.IsDefault || other.ActivationId.IsDefault || ActivationId.Equals(other.ActivationId));
        }

        public override int GetHashCode() => HashCode.Combine(this.SiloAddress, this.GrainId, this.ActivationId);

        public override string ToString() => $"[{nameof(GrainAddress)} GrainId {GrainId}, ActivationId: {ActivationId}, SiloAddress: {SiloAddress}]";

        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
            => destination.TryWrite($"[{nameof(GrainAddress)} GrainId {GrainId}, ActivationId: {ActivationId}, SiloAddress: {SiloAddress}]", out charsWritten);

        public string ToFullString() => $"[{nameof(GrainAddress)} GrainId {GrainId}, ActivationId: {ActivationId}, SiloAddress: {SiloAddress}, MembershipVersion: {MembershipVersion}]";

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
