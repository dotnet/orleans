using System;

namespace Orleans.Runtime
{
    [Serializable]
    internal class HashBasedPlacement : PlacementStrategy
    {
        internal static HashBasedPlacement Singleton { get; } = new HashBasedPlacement();
    }
}
