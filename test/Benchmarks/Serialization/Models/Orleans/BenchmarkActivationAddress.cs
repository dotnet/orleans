using System;
using Orleans;

namespace FakeFx.Runtime
{
    [Serializable, Immutable]
    [GenerateSerializer]
    [SuppressReferenceTracking]
    public sealed class BenchmarkActivationAddress : IEquatable<BenchmarkActivationAddress>
    {
        [Id(1)]
        public GrainId Grain { get; private set; }
        [Id(2)]
        public ActivationId Activation { get; private set; }
        [Id(3)]
        public SiloAddress Silo { get; private set; }

        public bool IsComplete => !Grain.IsDefault && Activation != null && Silo != null;

        private BenchmarkActivationAddress(SiloAddress silo, GrainId grain, ActivationId activation)
        {
            Silo = silo;
            Grain = grain;
            Activation = activation;
        }

        public static BenchmarkActivationAddress NewActivationAddress(SiloAddress silo, GrainId grain)
        {
            var activation = ActivationId.NewId();
            return GetAddress(silo, grain, activation);
        }

        public static BenchmarkActivationAddress GetAddress(SiloAddress silo, GrainId grain, ActivationId activation)
        {
            // Silo part is not mandatory
            if (grain.IsDefault)
            {
                throw new ArgumentNullException("grain");
            }

            return new BenchmarkActivationAddress(silo, grain, activation);
        }

        public override bool Equals(object obj) => Equals(obj as BenchmarkActivationAddress);

        public bool Equals(BenchmarkActivationAddress other) => other != null && Matches(other) && (Silo?.Equals(other.Silo) ?? other.Silo is null);

        public override int GetHashCode() => Grain.GetHashCode() ^ (Activation?.GetHashCode() ?? 0) ^ (Silo?.GetHashCode() ?? 0);

        public override string ToString() => $"[{Silo} {Grain} {Activation}]";

        public string ToFullString()
        {
            return
                string.Format(
                    "[ActivationAddress: {0}, Full GrainId: {1}, Full ActivationId: {2}]",
                    this.ToString(),                        // 0
                    this.Grain.ToString(),                  // 1
                    this.Activation.ToFullString());        // 2
        }

        public bool Matches(BenchmarkActivationAddress other)
        {
            return Grain.Equals(other.Grain) && (Activation?.Equals(other.Activation) ?? other.Activation is null);
        }
    }
}
