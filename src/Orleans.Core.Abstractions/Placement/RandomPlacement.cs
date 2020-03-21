using System;

namespace Orleans.Runtime
{
    [Serializable]
    public class RandomPlacement : PlacementStrategy
    {
        internal static RandomPlacement Singleton { get; } = new RandomPlacement();
    }
}
