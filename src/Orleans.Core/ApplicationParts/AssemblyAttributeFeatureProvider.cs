using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Orleans.Metadata;

namespace Orleans.ApplicationParts
{
    /// <summary>
    /// Populates a feature using assembly-level attributes which implement <see cref="IFeaturePopulator{TFeature}"/>.
    /// </summary>
    /// <typeparam name="TFeature">The feature type.</typeparam>
    public sealed class AssemblyAttributeFeatureProvider<TFeature> : IApplicationFeatureProvider<TFeature>
    {
        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == this.GetType();
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return typeof(AssemblyAttributeFeatureProvider<TFeature>).GetHashCode();
        }

        /// <inheritdoc />
        public void PopulateFeature(IEnumerable<IApplicationPart> parts, TFeature feature)
        {
            foreach (var part in parts.OfType<AssemblyPart>())
            {
                var attributes = part.Assembly.GetCustomAttributes<FeaturePopulatorAttribute>();
                foreach (var attribute in attributes)
                {
                    if (!typeof(IFeaturePopulator<TFeature>).IsAssignableFrom(attribute.PopulatorType)) continue;

                    var populator = (IFeaturePopulator<TFeature>) Activator.CreateInstance(attribute.PopulatorType);
                    populator.Populate(feature);
                }
            }
        }
    }
}