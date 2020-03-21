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

        /// <summary>
        /// Returns a value indicating whether or not activations using this strategy must have deterministic activation ids.
        /// If true then activations have activation ids equal to their grain id, otherwise activations are given unique ids.
        /// </summary>
        internal virtual bool IsDeterministicActivationId => false;
    }
}
