using System;
using System.Collections.Generic;
using Orleans.Metadata;

namespace Orleans.Runtime
{
    /// <summary>
    /// The base type for all placement strategies.
    /// </summary>
    /// <remarks>
    /// Orleans uses a configurable placement system to decide which server to place a grain on.
    /// Placement directors are used to decide where a grain activation should be placed.
    /// Placement directors are associated with grains using a placement strategy.
    /// Grains indicate their preferred placement strategy using an attribute on the grain class.
    /// </remarks>
    [Serializable]
    [GenerateSerializer]
    public abstract class PlacementStrategy
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PlacementStrategy"/> class.
        /// </summary>
        protected PlacementStrategy()
        {
        }

        /// <summary>
        /// Gets a value indicating whether or not this placement strategy requires activations to be registered in
        /// the grain directory.
        /// </summary>
        public virtual bool IsUsingGrainDirectory => true;

        /// <summary>
        /// Initializes an instance of this type using the provided grain properties.
        /// </summary>
        /// <param name="properties">
        /// The grain properties.
        /// </param>
        public virtual void Initialize(GrainProperties properties)
        {
        }

        /// <summary>
        /// Populates grain properties to specify the preferred placement strategy.
        /// </summary>
        /// <param name="services">The service provider.</param>
        /// <param name="grainClass">The grain class.</param>
        /// <param name="grainType">The grain type.</param>
        /// <param name="properties">The grain properties which will be populated by this method call.</param>
        public virtual void PopulateGrainProperties(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            properties[WellKnownGrainTypeProperties.PlacementStrategy] = this.GetType().Name;
        }
    }
}
