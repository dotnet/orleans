using System;

namespace Orleans.Runtime
{
    [Serializable]
    public class PreferLocalPlacement : PlacementStrategy
    {
        internal static PreferLocalPlacement Singleton { get; } = new PreferLocalPlacement();
    }
}
