using System;

namespace Orleans.Runtime
{
    [Serializable]
    public class ActivationCountBasedPlacement : PlacementStrategy
    {
        internal static ActivationCountBasedPlacement Singleton { get; } = new ActivationCountBasedPlacement();
    }
}
