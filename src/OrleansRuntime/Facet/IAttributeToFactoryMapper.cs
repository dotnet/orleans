using System.Reflection;

namespace Orleans.Runtime
{
    /// <summary>
    /// Responsible for mapping an attribute to a cachable factory.
    /// </summary>
    public interface IAttributeToFactoryMapper<in TAttribute>
        where TAttribute : FacetAttribute
    {
        /// <summary>
        /// Responsible for mapping an attribute to a cachable factory from the parameter and the attribute.
        /// </summary>
        Factory<IGrainActivationContext, object> GetFactory(ParameterInfo parameter, TAttribute attribute);
    }
}
