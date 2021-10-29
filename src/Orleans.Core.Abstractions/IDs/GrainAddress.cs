using System;
using Orleans.GrainDirectory;
using Orleans.Runtime;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents an entry in a <see cref="IGrainDirectory"/>
    /// </summary>
    public class GrainAddress
    {
        /// <summary>
        /// Identifier of the Grain
        /// </summary>
        public GrainId GrainId { get; set; }

        /// <summary>
        /// Id of the specific Grain activation
        /// </summary>
        public ActivationId ActivationId { get; set; }

        /// <summary>
        /// Address of the silo where the grain activation lives
        /// </summary>
        public SiloAddress SiloAddress { get; set; }

        /// <summary>
        /// MembershipVersion at the time of registration
        /// </summary>
        public MembershipVersion MembershipVersion { get; set; } = MembershipVersion.MinValue;

        public override bool Equals(object obj)
        {
            return obj is GrainAddress address &&
                   this.Matches(address) &&
                   this.MembershipVersion == address.MembershipVersion;
        }

        /// <summary>
        /// Two grain addresses match if they are equal ignoring their <see cref="MembershipVersion"/> value.
        /// </summary>
        /// <param name="address"> The other GrainAddress to compare this one with.</param>
        /// <returns> Returns <c>true</c> if the two GrainAddress are considered to match</returns>
        public bool Matches(GrainAddress address)
        {
            return this.SiloAddress == address.SiloAddress &&
                   this.GrainId == address.GrainId &&
                   this.ActivationId == address.ActivationId;
        }

        public override int GetHashCode() => HashCode.Combine(this.SiloAddress, this.GrainId, this.ActivationId);

        internal static GrainAddress NewActivationAddress(SiloAddress silo, GrainId grain)
        {
            var activation = ActivationId.NewId();
            return GetAddress(silo, grain, activation);
        }

        internal static GrainAddress GetAddress(SiloAddress silo, GrainId grain, ActivationId activation)
        {
            // Silo part is not mandatory
            if (grain.IsDefault) throw new ArgumentNullException("grain");

            return new GrainAddress
            {
                GrainId = grain,
                ActivationId = activation,
                SiloAddress = silo,
            };
        }
    }
}
