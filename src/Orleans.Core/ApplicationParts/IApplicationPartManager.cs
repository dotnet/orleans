using System;
using System.Collections.Generic;

namespace Orleans.ApplicationParts
{
    /// <summary>
    /// Builder for configuring application parts.
    /// </summary>
    public interface IApplicationPartManager
    {
        /// <summary>
        /// Gets the list of <see cref="IApplicationFeatureProvider"/>s.
        /// </summary>
        IReadOnlyList<IApplicationFeatureProvider> FeatureProviders { get; }

        /// <summary>
        /// Gets the list of <see cref="IApplicationPart"/>s.
        /// </summary>
        IReadOnlyList<IApplicationPart> ApplicationParts { get; }

        /// <summary>
        /// Adds an application part.
        /// </summary>
        /// <param name="part">The application part.</param>
        IApplicationPartManager AddApplicationPart(IApplicationPart part);

        /// <summary>
        /// Removes application parts matching a predicate.
        /// </summary>
        /// <param name="predicate">The predicate used to select which application parts to remove.</param>
        IApplicationPartManager RemoveApplicationParts(Func<IApplicationPart, bool> predicate);

        /// <summary>
        /// Adds a feature provider.
        /// </summary>
        /// <param name="featureProvider">The feature provider.</param>
        IApplicationPartManager AddFeatureProvider(IApplicationFeatureProvider featureProvider);

        /// <summary>
        /// Removes feature providers matching a predicate.
        /// </summary>
        /// <param name="predicate">The predicate used to select which feature providers to remove.</param>
        IApplicationPartManager RemoveFeatureProviders(Func<IApplicationFeatureProvider, bool> predicate);

        /// <summary>
        /// Populates the given <paramref name="feature"/> using the list of
        /// <see cref="IApplicationFeatureProvider{TFeature}"/>s configured on the
        /// <see cref="ApplicationPartManager"/>.
        /// </summary>
        /// <typeparam name="TFeature">The type of the feature.</typeparam>
        /// <param name="feature">The feature instance to populate.</param>
        void PopulateFeature<TFeature>(TFeature feature);
    }
}