using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// The prefer local placement strategy indicates that a grain should always be placed on the local host if the grain
    /// is not already active elsewhere in the cluster and the local host is compatible with it.
    /// </summary>
    /// <remarks>
    /// If the host is not compatible with the grain type or if a grain receives an incompatible request, the grain will be
    /// placed on a random, compatible server.
    /// </remarks>
    [Serializable]
    [GenerateSerializer]
    public class PreferLocalPlacement : PlacementStrategy
    {
        /// <summary>
        /// Gets the singleton instance of this class.
        /// </summary>
        internal static PreferLocalPlacement Singleton { get; } = new PreferLocalPlacement();
    }
}
