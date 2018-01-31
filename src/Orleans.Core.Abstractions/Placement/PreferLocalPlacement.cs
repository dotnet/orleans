using System;

namespace Orleans.Runtime
{
    [Serializable]
    internal class PreferLocalPlacement : PlacementStrategy
    {
        internal static PreferLocalPlacement Singleton { get; } = new PreferLocalPlacement();
    }
}
