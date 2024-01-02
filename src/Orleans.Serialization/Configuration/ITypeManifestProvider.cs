using System;
using Microsoft.Extensions.Options;

namespace Orleans.Serialization.Configuration
{
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
                try
                {
                    ConfigureInner(options);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException($"Error configuring Orleans.Serialization for '{Key}'. See InnerException for details.", exception);
                }
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
