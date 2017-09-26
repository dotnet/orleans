using System;

namespace Orleans.Runtime
{
    [Serializable]
    public class ActivationCountBasedPlacement : PlacementStrategy
    {
        public static ActivationCountBasedPlacement Singleton { get; } = new ActivationCountBasedPlacement();

        private ActivationCountBasedPlacement()
        {}
        
        public override bool Equals(object obj)
        {
            return obj is ActivationCountBasedPlacement;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode();
        }
    }
}
