using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.ApplicationParts
{
    /// <summary>
    /// Manages the parts and features of an Orleans application.
    /// </summary>
    public class ApplicationPartManager : IApplicationPartManager
    {
        private readonly List<IApplicationPart> applicationParts = new List<IApplicationPart>();
        private readonly List<IApplicationFeatureProvider> featureProviders = new List<IApplicationFeatureProvider>();

        /// <inheritdoc />
        public IReadOnlyList<IApplicationFeatureProvider> FeatureProviders => this.featureProviders;

        /// <inheritdoc />
        public IReadOnlyList<IApplicationPart> ApplicationParts => this.applicationParts;

        /// <inheritdoc />
        public IApplicationPartManager AddApplicationPart(IApplicationPart part)
        {
            if (!this.applicationParts.Contains(part)) this.applicationParts.Add(part);
            return this;
        }

        /// <inheritdoc />
        public IApplicationPartManager RemoveApplicationParts(Func<IApplicationPart, bool> predicate)
        {
            this.applicationParts.RemoveAll(part => predicate(part));
            return this;
        }

        /// <inheritdoc />
        public IApplicationPartManager AddFeatureProvider(IApplicationFeatureProvider featureProvider)
        {
            if (!this.featureProviders.Contains(featureProvider)) this.featureProviders.Add(featureProvider);
            return this;
        }

        /// <inheritdoc />
        public IApplicationPartManager RemoveFeatureProviders(Func<IApplicationFeatureProvider, bool> predicate)
        {
            this.featureProviders.RemoveAll(provider => predicate(provider));
            return this;
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
