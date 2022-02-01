using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// The random placement strategy specifies that new activations of a grain should be placed on a random, compatible server.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class RandomPlacement : PlacementStrategy
    {
        /// <summary>
        /// Gets the singleton instance of this class.
        /// </summary>
        internal static RandomPlacement Singleton { get; } = new RandomPlacement();
    }
}
