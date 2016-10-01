using System;
using Newtonsoft.Json;
using Orleans.GrainDirectory;

namespace Orleans.Runtime
{
    [Serializable]
    internal class ActivationAddress
    {
        public GrainId Grain { get; private set; }
        public ActivationId Activation { get; private set; }
        public SiloAddress Silo { get; private set; }

        public bool IsComplete
        {
            get { return Grain != null && Activation != null && Silo != null; }
        }

        [JsonConstructor]
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
            if (grain == null) throw new ArgumentNullException("grain");

            return new ActivationAddress(silo, grain, activation);
        }

        public override bool Equals(object obj)
        {
            var other = obj as ActivationAddress;
            return other != null && Equals(Silo, other.Silo) && Equals(Grain, other.Grain) && Equals(Activation, other.Activation);
        }

        public override int GetHashCode()
        {
            return (Silo != null ? Silo.GetHashCode() : 0) ^
                (Grain != null ? Grain.GetHashCode() : 0) ^
                (Activation != null ? Activation.GetHashCode() : 0);
        }

        public override string ToString()
        {
            return String.Format("{0}{1}{2}", Silo, Grain, Activation);
        }

        public string ToFullString()
        {
            return
                String.Format(
                    "[ActivationAddress: {0}, Full GrainId: {1}, Full ActivationId: {2}]",
                    this.ToString(),                        // 0
                    this.Grain.ToFullString(),              // 1
                    this.Activation.ToFullString());        // 2
        }

        public bool Matches(ActivationAddress other)
        {
            return Equals(Grain, other.Grain) && Equals(Activation, other.Activation);
        }
    }
}
