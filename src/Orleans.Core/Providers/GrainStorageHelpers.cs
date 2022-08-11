using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers;
using Orleans.Runtime;

#nullable enable
namespace Orleans.Storage
{
    /// <summary>
    /// Utility functions for grain storage.
    /// </summary>
    public static class GrainStorageHelpers
    {
        /// <summary>
        /// Gets the <see cref="IGrainStorage"/> associated with the specified grain type, which must derive from <see cref="Grain{T}"/>.
        /// </summary>
        /// <param name="grainType">The grain type, which must derive from <see cref="Grain{T}"/>.</param>
        /// <param name="services">The service provider.</param>
        /// <returns>
        /// The <see cref="IGrainStorage"/> associated with the specified grain type, which must derive from <see cref="Grain{T}"/>.
        /// </returns>
        public static IGrainStorage GetGrainStorage(Type grainType, IServiceProvider services)
        {
            if (grainType is null) throw new ArgumentNullException(nameof(grainType));
            var attrs = grainType.GetCustomAttributes(typeof(StorageProviderAttribute), true);
            var attr = attrs.Length > 0 ? (StorageProviderAttribute)attrs[0] : null;
            var storageProvider = attr != null
                ? services.GetServiceByName<IGrainStorage>(attr.ProviderName)
                : services.GetService<IGrainStorage>();
            if (storageProvider == null)
            {
                ThrowMissingProviderException(grainType, attr?.ProviderName);
            }

            return storageProvider;
        }

        [DoesNotReturn]
        private static void ThrowMissingProviderException(Type grainType, string? name)
        {
            var grainTypeName = grainType.FullName;
            var errMsg = string.IsNullOrEmpty(name)
                ? $"No default storage provider found loading grain type {grainTypeName}."
                : $"No storage provider named \"{name}\" found loading grain type {grainTypeName}.";
            throw new BadProviderConfigException(errMsg);
        }
    }
}
