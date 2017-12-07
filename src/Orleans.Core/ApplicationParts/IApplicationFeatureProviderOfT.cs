using System.Collections.Generic;

namespace Orleans.ApplicationParts
{
    /// <summary>
    /// A provider for a given <typeparamref name="TFeature"/> feature.
    /// </summary>
    /// <typeparam name="TFeature">The type of the feature.</typeparam>
    public interface IApplicationFeatureProvider<in TFeature> : IApplicationFeatureProvider
    {
        /// <summary>
        /// Updates the <paramref name="feature"/> instance.
        /// </summary>
        /// <param name="parts">The list of <see cref="IApplicationPart"/>s of the
        /// application.
        /// </param>
        /// <param name="feature">The feature instance to populate.</param>
        void PopulateFeature(IEnumerable<IApplicationPart> parts, TFeature feature);
    }
}
