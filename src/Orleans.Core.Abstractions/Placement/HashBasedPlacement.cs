using System;

namespace Orleans.Runtime
{
    [Serializable]
    internal class HashBasedPlacement : PlacementStrategy
    {

        internal static HashBasedPlacement Singleton { get; } = new HashBasedPlacement();


        public HashBasedPlacement()
        {
        }

        public override bool Equals(object obj)
        {
            return obj is HashBasedPlacement;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode();
        }
    }
}
