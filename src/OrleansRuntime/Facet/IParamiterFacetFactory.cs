using System.Reflection;

namespace Orleans.Runtime
{
    /// <summary>
    /// Responsible for creating a cachable facet factory using the information from the parameter
    ///   and it's facet attribute.
    /// </summary>
    public interface IParameterFacetFactory<in TAttribute>
        where TAttribute : FacetAttribute
    {
        /// <summary>
        /// Responsible for creating a cachable facet factory using the information from the parameter
        ///   and it's facet attribute.
        /// </summary>
        Factory<IGrainActivationContext, object> Create(ParameterInfo parameter, TAttribute attribute);
    }
}
