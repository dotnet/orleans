using System;

namespace Orleans.Runtime
{
    [Serializable]
    [GenerateSerializer]
    public class ActivationCountBasedPlacement : PlacementStrategy
    {
        internal static ActivationCountBasedPlacement Singleton { get; } = new ActivationCountBasedPlacement();
    }
}
