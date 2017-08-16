using System.Reflection;

namespace Orleans.Runtime
{
    /// <summary>
    /// Responsible for mapping a facet metadata to a cachable factory.
    /// </summary>
    public interface IAttributeToFactoryMapper<in TMetadata>
        where TMetadata : IFacetMetadata
    {
        /// <summary>
        /// Responsible for mapping a facet metadata to a cachable factory from the parameter and facet metadata.
        /// </summary>
        Factory<IGrainActivationContext, object> GetFactory(ParameterInfo parameter, TMetadata metadata);
    }
}
