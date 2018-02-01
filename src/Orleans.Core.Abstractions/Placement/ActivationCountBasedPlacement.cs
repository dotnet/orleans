using System;

namespace Orleans.Runtime
{
    [Serializable]
    internal class ActivationCountBasedPlacement : PlacementStrategy
    {
        internal static ActivationCountBasedPlacement Singleton { get; } = new ActivationCountBasedPlacement();
    }
}
