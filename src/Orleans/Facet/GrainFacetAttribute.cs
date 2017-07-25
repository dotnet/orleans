
using System;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Base class for any attribution of grain facets
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public abstract class GrainFacetAttribute : Attribute
    {
        /// <summary>
        /// Acquires factory deligate for the type of facet being created.
        /// </summary>
        public abstract Factory<IGrainActivationContext, object> GetFactory(Type parameterType, string parameterName);
    }
}
