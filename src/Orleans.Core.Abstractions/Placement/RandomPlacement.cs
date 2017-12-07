using System;

namespace Orleans.Runtime
{
    [Serializable]
    internal class RandomPlacement : PlacementStrategy
    {
        internal static RandomPlacement Singleton { get; } = new RandomPlacement();

        private RandomPlacement()
        { }

        public override bool Equals(object obj)
        {
            return obj is RandomPlacement;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode();
        }
    }
}
