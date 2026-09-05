using System;
using System.Collections.Generic;
#if NET5_0_OR_GREATER
using System.Diagnostics.CodeAnalysis;
#endif
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
#if NET5_0_OR_GREATER
        private const DynamicallyAccessedMemberTypes ImplementationTypeMembers =
            DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces;

        private const DynamicallyAccessedMemberTypes InterfaceTypeMembers =
            DynamicallyAccessedMemberTypes.PublicMethods
            | DynamicallyAccessedMemberTypes.NonPublicMethods
            | DynamicallyAccessedMemberTypes.Interfaces;
#endif

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

        /// <summary>
        /// Adds a generated implementation type to a manifest collection and preserves the members used to inspect and activate it.
        /// </summary>
        /// <param name="collection">The manifest collection.</param>
        /// <param name="type">The generated implementation type.</param>
        protected static void AddImplementationType(
            HashSet<Type> collection,
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(ImplementationTypeMembers)]
#endif
            Type type) => collection.Add(type);

        /// <summary>
        /// Adds a generated interface type to a manifest collection and preserves the methods and inherited interfaces used by generated invokables.
        /// </summary>
        /// <param name="collection">The manifest collection.</param>
        /// <param name="type">The generated interface type.</param>
        protected static void AddInterfaceType(
            HashSet<Type> collection,
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(InterfaceTypeMembers)]
#endif
            Type type) => collection.Add(type);

        /// <summary>
        /// Adds a generated proxy type to a manifest collection and preserves its implemented interfaces.
        /// </summary>
        /// <param name="collection">The manifest collection.</param>
        /// <param name="type">The generated proxy type.</param>
        protected static void AddMetadataType(
            HashSet<Type> collection,
#if NET5_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
#endif
            Type type) => collection.Add(type);
    }
}
