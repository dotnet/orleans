using System;

namespace Orleans.Serialization.Configuration
{
    /// <summary>
    /// Defines a metadata provider for this assembly.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class TypeManifestProviderAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TypeManifestProviderAttribute"/> class.
        /// </summary>
        /// <param name="providerType">The metadata provider type.</param>
        public TypeManifestProviderAttribute(Type providerType)
        {
            if (providerType is null)
            {
                throw new ArgumentNullException(nameof(providerType));
            }

            if (!typeof(ITypeManifestProvider).IsAssignableFrom(providerType))
            {
                throw new ArgumentException($"Provided type {providerType} must implement {typeof(ITypeManifestProvider)}", nameof(providerType));
            }

            ProviderType = providerType;
        }

        /// <summary>
        /// Gets the manifest provider type.
        /// </summary>
        public Type ProviderType { get; }
    }
}