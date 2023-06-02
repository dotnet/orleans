using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Places activations on compatible silos by hashing the grain identifier using a stable hash and selecting a silo from a sorted set using a modulo operation.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable, SuppressReferenceTracking]
    public sealed class HashBasedPlacement : PlacementStrategy
    {
        internal static HashBasedPlacement Singleton { get; } = new HashBasedPlacement();
    }
}
