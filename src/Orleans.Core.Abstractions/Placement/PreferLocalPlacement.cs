using System;

namespace Orleans.Runtime
{
    [Serializable]
    [GenerateSerializer]
    public class PreferLocalPlacement : PlacementStrategy
    {
        internal static PreferLocalPlacement Singleton { get; } = new PreferLocalPlacement();
    }
}
