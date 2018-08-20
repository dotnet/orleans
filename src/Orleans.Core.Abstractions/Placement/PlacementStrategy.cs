using System;

namespace Orleans.Runtime
{
    [Serializable]
    public abstract class PlacementStrategy
    {
        /// <summary>
        /// Returns a value indicating whether or not this placement strategy requires activations to be registered in
        /// the grain directory.
        /// </summary>
        public virtual bool IsUsingGrainDirectory => true;

        public virtual bool IsDeterministicActivationId => false;
    }
}
