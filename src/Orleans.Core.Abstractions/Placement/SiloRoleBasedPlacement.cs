using System;

namespace Orleans.Runtime
{
    [Serializable]
    public class SiloRoleBasedPlacement : PlacementStrategy
    {
        internal static SiloRoleBasedPlacement Singleton { get; } = new SiloRoleBasedPlacement();
    }
}
