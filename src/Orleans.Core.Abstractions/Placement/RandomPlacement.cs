using System;

namespace Orleans.Runtime
{
    [Serializable]
    internal class RandomPlacement : PlacementStrategy
    {
        internal static RandomPlacement Singleton { get; } = new RandomPlacement();
    }
}
