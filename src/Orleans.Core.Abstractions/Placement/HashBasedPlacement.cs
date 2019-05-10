using System;

namespace Orleans.Runtime
{
    [Serializable]
    public class HashBasedPlacement : PlacementStrategy
    {
        internal static HashBasedPlacement Singleton { get; } = new HashBasedPlacement();
    }
}
