using System;

namespace Orleans.Runtime
{
    [Serializable]
    [GenerateSerializer]
    public class HashBasedPlacement : PlacementStrategy
    {
        internal static HashBasedPlacement Singleton { get; } = new HashBasedPlacement();
    }
}
