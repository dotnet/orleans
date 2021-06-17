using System;
using System.Collections.Generic;
using Orleans.Metadata;

namespace Orleans.Runtime
{
    [Serializable]
    [GenerateSerializer]
    public abstract class PlacementStrategy
    {
        protected PlacementStrategy()
        {
        }

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

        public virtual void Initialize(GrainProperties properties)
        {
        }

        public virtual void PopulateGrainProperties(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            properties[WellKnownGrainTypeProperties.PlacementStrategy] = this.GetType().Name;
        }
    }
}
