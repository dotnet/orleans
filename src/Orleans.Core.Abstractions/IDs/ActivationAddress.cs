using System;

namespace Orleans.Runtime
{
    [Serializable, Immutable]
    [GenerateSerializer]
    [SuppressReferenceTracking]
    public sealed class ActivationAddress : IEquatable<ActivationAddress>
    {
        [Id(1)]
        public GrainId Grain { get; private set; }
        [Id(2)]
        public ActivationId Activation { get; private set; }
        [Id(3)]
        public SiloAddress Silo { get; private set; }

        public bool IsComplete => !Grain.IsDefault && !Activation.IsDefault && Silo != null;

        private ActivationAddress(SiloAddress silo, GrainId grain, ActivationId activation)
        {
            Silo = silo;
            Grain = grain;
            Activation = activation;
        }

        public static ActivationAddress NewActivationAddress(SiloAddress silo, GrainId grain)
        {
            var activation = ActivationId.NewId();
            return GetAddress(silo, grain, activation);
        }

        public static ActivationAddress GetAddress(SiloAddress silo, GrainId grain, ActivationId activation)
        {
            // Silo part is not mandatory
            if (grain.IsDefault) throw new ArgumentNullException("grain");

            return new ActivationAddress(silo, grain, activation);
        }

        public override bool Equals(object obj) => Equals(obj as ActivationAddress);

        public bool Equals(ActivationAddress other) => other != null && Matches(other) && (Silo?.Equals(other.Silo) ?? other.Silo is null);

        public override int GetHashCode() => Grain.GetHashCode() ^ Activation.GetHashCode() ^ (Silo?.GetHashCode() ?? 0);

        public override string ToString() => $"[{Silo} {Grain} {Activation}]";

        public string ToFullString()
        {
            return
                String.Format(
                    "[ActivationAddress: {0}, Full GrainId: {1}, Full ActivationId: {2}]",
                    this.ToString(),                        // 0
                    this.Grain.ToString(),                  // 1
                    this.Activation.ToFullString());        // 2
        }

        public bool Matches(ActivationAddress other) => Grain.Equals(other.Grain) && Activation.Equals(other.Activation);
    }
}
