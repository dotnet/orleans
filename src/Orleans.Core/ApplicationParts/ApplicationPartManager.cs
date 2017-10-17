using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.ApplicationParts
{
    /// <summary>
    /// Manages the parts and features of an Orleans application.
    /// </summary>
    public class ApplicationPartManager
    {
        private readonly List<IApplicationPart> applicationParts = new List<IApplicationPart>();
        private readonly List<IApplicationFeatureProvider> featureProviders = new List<IApplicationFeatureProvider>();

        /// <summary>
        /// Gets the list of <see cref="IApplicationFeatureProvider"/>s.
        /// </summary>
        public IReadOnlyList<IApplicationFeatureProvider> FeatureProviders => this.featureProviders;

        /// <summary>
        /// Gets the list of <see cref="IApplicationPart"/>s.
        /// </summary>
        public IReadOnlyList<IApplicationPart> ApplicationParts => this.applicationParts;

        /// <summary>
        /// Adds an application part.
        /// </summary>
        /// <param name="part">The application part.</param>
        public void AddApplicationPart(IApplicationPart part)
        {
            if (!this.applicationParts.Contains(part)) this.applicationParts.Add(part);
        }

        /// <summary>
        /// Adds a feature provider.
        /// </summary>
        /// <param name="featureProvider">The feature provider.</param>
        public void AddFeatureProvider(IApplicationFeatureProvider featureProvider)
        {
            if (!this.featureProviders.Contains(featureProvider)) this.featureProviders.Add(featureProvider);
        }

        /// <summary>
        /// Populates the given <paramref name="feature"/> using the list of
        /// <see cref="IApplicationFeatureProvider{TFeature}"/>s configured on the
        /// <see cref="ApplicationPartManager"/>.
        /// </summary>
        /// <typeparam name="TFeature">The type of the feature.</typeparam>
        /// <param name="feature">The feature instance to populate.</param>
        public void PopulateFeature<TFeature>(TFeature feature)
        {
            if (feature == null)
            {
                throw new ArgumentNullException(nameof(feature));
            }

            foreach (var provider in FeatureProviders.OfType<IApplicationFeatureProvider<TFeature>>())
            {
                provider.PopulateFeature(ApplicationParts, feature);
            }
        }
    }
}
