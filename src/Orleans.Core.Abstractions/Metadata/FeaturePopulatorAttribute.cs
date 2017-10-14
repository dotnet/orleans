using System;
using System.Linq;

namespace Orleans.Metadata
{
    /// <summary>
    /// Defines a feature populator for this assembly.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class FeaturePopulatorAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FeaturePopulatorAttribute"/> class.
        /// </summary>
        /// <param name="populatorType">The feature populator type.</param>
        public FeaturePopulatorAttribute(Type populatorType)
        {
            if (populatorType == null) throw new ArgumentNullException(nameof(populatorType));
            if (!populatorType.GetInterfaces().Any(iface => iface.IsConstructedGenericType && typeof(IFeaturePopulator<>).IsAssignableFrom(iface.GetGenericTypeDefinition())))
            {
                throw new ArgumentException($"Provided type {populatorType} must implement {typeof(IFeaturePopulator<>)}", nameof(populatorType));
            }

            this.PopulatorType = populatorType;
        }

        /// <summary>
        /// Gets the feature populator type.
        /// </summary>
        public Type PopulatorType { get; }
    }
}