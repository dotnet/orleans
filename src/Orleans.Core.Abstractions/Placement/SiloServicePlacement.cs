using System;

namespace Orleans.Runtime
{
    [Serializable]
    internal class SiloServicePlacement : PlacementStrategy
    {
        internal static SiloServicePlacement Singleton { get; } = new SiloServicePlacement();

        /// <inheritdoc />
        public override bool IsUsingGrainDirectory => false;

        /// <inheritdoc />
        internal override bool IsDeterministicActivationId => true;
    }
}