using System;

namespace Orleans.Runtime
{
    [Serializable]
    public class RandomPlacement : PlacementStrategy
    {
        public static RandomPlacement Singleton { get; } = new RandomPlacement();

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
