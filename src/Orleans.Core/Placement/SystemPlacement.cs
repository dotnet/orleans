using System;

namespace Orleans.Runtime
{
    [Serializable]
    internal class SystemPlacement : PlacementStrategy
    {
        internal static SystemPlacement Singleton { get; } = new SystemPlacement();
    }
}
