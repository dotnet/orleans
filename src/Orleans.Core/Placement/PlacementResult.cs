using System;


namespace Orleans.Runtime
{
    internal class PlacementResult
    {
        public PlacementStrategy PlacementStrategy { get; private set; }

        public bool IsNewPlacement => PlacementStrategy != null;

        public ActivationId Activation { get; private set; }
        public SiloAddress Silo { get; private set; }

        /// <summary>
        /// Some storage providers need to know the grain type in order to read the state.
        /// The PlacementResult is generated based on the target grain type's policy, so the type
        /// is known and will be passed in the message NewGrainType header.
        /// </summary>
        public string GrainType { get; private set; }

        private PlacementResult()
        {
        }

        public static PlacementResult IdentifySelection(ActivationAddress address)
        {
            return new PlacementResult
            {
                Activation = address.Activation,
                Silo = address.Silo
            };
        }

        public static PlacementResult SpecifyCreation(
            SiloAddress silo,
            ActivationId activationId,
            PlacementStrategy placement,
            string grainType)
        {
            if (silo == null)
                throw new ArgumentNullException(nameof(silo));
            if (activationId == null)
                throw new ArgumentNullException(nameof(activationId));
            if (placement == null)
                throw new ArgumentNullException(nameof(placement));
            if (string.IsNullOrWhiteSpace(grainType))
                throw new ArgumentException("'grainType' must contain a valid type name.", nameof(grainType));

            return new PlacementResult
            {
                Activation = activationId,
                Silo = silo,
                PlacementStrategy = placement,
                GrainType = grainType
            };
        }

        public ActivationAddress ToAddress(GrainId grainId)
        {
            return ActivationAddress.GetAddress(Silo, grainId, Activation);
        }

        public override string ToString()
        {
            var placementStr = IsNewPlacement ? PlacementStrategy.ToString() : "*not-new*";
            return $"PlacementResult({this.Silo}, {this.Activation}, {placementStr}, {this.GrainType})";
        }
    }
}