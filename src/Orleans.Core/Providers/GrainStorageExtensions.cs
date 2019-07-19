using Orleans.Providers;
using Orleans.Runtime;
using System;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;

namespace Orleans.Storage
{
    public static class GrainStorageExtensions
    {
        /// <summary>
        /// Acquire the storage provider associated with the grain type.
        /// </summary>
        /// <returns></returns>
        public static IGrainStorage GetGrainStorage(this Grain grain, IServiceProvider services) 
            => GetGrainStorage(grain.GetType(), services);

        /// <summary>
        /// Aquire the storage provider associated with the grain type.
        /// </summary>
        /// <returns></returns>
        public static IGrainStorage GetGrainStorage(this Type grainType, IServiceProvider services)
        {
            var attr = grainType.GetCustomAttributes<StorageProviderAttribute>(true).FirstOrDefault();
            IGrainStorage storageProvider = attr != null
                ? services.GetServiceByName<IGrainStorage>(attr.ProviderName)
                : services.GetService<IGrainStorage>();
            if (storageProvider == null)
            {
                ThrowMissingProviderException(grainType, attr?.ProviderName);
            }
            return storageProvider;
        }

        private static void ThrowMissingProviderException(Type grainType, string name)
        {
            var grainTypeName = grainType.GetParseableName(TypeFormattingOptions.LogFormat);
            var errMsg = string.IsNullOrEmpty(name)
                ? $"No default storage provider found loading grain type {grainTypeName}."
                : $"No storage provider named \"{name}\" found loading grain type {grainTypeName}.";
            throw new BadGrainStorageConfigException(errMsg);
        }
    }
}
