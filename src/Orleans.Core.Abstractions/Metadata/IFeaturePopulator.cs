using System;

namespace Orleans.Metadata
{
    /// <summary>
    /// Populates a specified kind of feature.
    /// </summary>
    /// <typeparam name="TFeature">The type of the feature.</typeparam>
    public interface IFeaturePopulator<in TFeature>
    {
        /// <summary>
        /// Populates the provided <paramref name="feature"/>.
        /// </summary>
        /// <param name="feature">The feature.</param>
        void Populate(TFeature feature);
    }
}