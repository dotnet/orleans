using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Strategy that applies to an individual grain
    /// </summary>
    [Serializable]
    internal abstract class GrainStrategy
    {
        /// <summary>
        /// Placement strategy that indicates that new activations of this grain type should be placed randomly,
        /// subject to the overall placement policy.
        /// </summary>
        public static PlacementStrategy RandomPlacement;
        /// <summary>
        /// Placement strategy that indicates that new activations of this grain type should be placed on a local silo.
        /// </summary>
        public static PlacementStrategy PreferLocalPlacement;

        /// <summary>
        /// Placement strategy that indicates that new activations of this grain type should be placed
        /// subject to the current load distribution across the deployment.
        /// This Placement that takes into account CPU/Memory/ActivationCount.
        /// </summary>
        public static PlacementStrategy ActivationCountBasedPlacement;
        /// <summary>
        /// Use a graph partitioning algorithm
        /// </summary>
        internal static PlacementStrategy GraphPartitionPlacement;

        internal static void InitDefaultGrainStrategies()
        {
            RandomPlacement = Orleans.Runtime.RandomPlacement.Singleton;

            PreferLocalPlacement = Orleans.Runtime.PreferLocalPlacement.Singleton;

            ActivationCountBasedPlacement = Orleans.Runtime.ActivationCountBasedPlacement.Singleton;
        }
    }
}
