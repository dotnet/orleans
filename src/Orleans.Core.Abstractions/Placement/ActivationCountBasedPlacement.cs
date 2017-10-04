using System;

namespace Orleans.Runtime
{
    [Serializable]
    internal class ActivationCountBasedPlacement : PlacementStrategy
    {
        internal static ActivationCountBasedPlacement Singleton { get; } = new ActivationCountBasedPlacement();

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
