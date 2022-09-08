using System;

namespace Orleans.Runtime
{
    [Serializable, GenerateSerializer, Immutable]
    public sealed class HashBasedPlacement : PlacementStrategy
    {
        internal static HashBasedPlacement Singleton { get; } = new HashBasedPlacement();
    }
}
