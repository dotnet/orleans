using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Orleans.Serialization.Configuration
{
    /// <summary>
    /// Provides metadata for configuration-driven Orleans providers.
    /// </summary>
    public interface IProviderMetadataProvider
    {
        /// <summary>
        /// Adds known providers to <paramref name="providers"/>.
        /// </summary>
        /// <param name="providers">The provider registrations, keyed by target, kind, and name.</param>
        void ConfigureProviders(IDictionary<(string Target, string Kind, string Name), Type> providers);
    }

    /// <summary>
    /// Provides type manifest information.
    /// </summary>
    public interface ITypeManifestProvider : IConfigureOptions<TypeManifestOptions>
    {
    }

    /// <summary>
    /// Base class for generated type manifest providers.
    /// </summary>
    public abstract class TypeManifestProviderBase : ITypeManifestProvider
    {
        /// <inheritdoc/>
        void IConfigureOptions<TypeManifestOptions>.Configure(TypeManifestOptions options)
        {
            if (options.TypeManifestProviders.Add(Key))
            {
                ConfigureInner(options);
            }
        }

        /// <summary>
        /// Gets the unique identifier for this type manifest provider.
        /// </summary>
        public virtual object Key => GetType();

        /// <summary>
        /// Configures the provided type manifest options.
        /// </summary>
        /// <param name="options">The type manifest options.</param>
        protected abstract void ConfigureInner(TypeManifestOptions options);
    }
}
