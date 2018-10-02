﻿using Orleans.Providers;
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
        {
            StorageProviderAttribute attr = grain.GetType().GetCustomAttributes<StorageProviderAttribute>(true).FirstOrDefault();
            IGrainStorage storageProvider = attr != null
                ? services.GetServiceByName<IGrainStorage>(attr.ProviderName)
                : services.GetService<IGrainStorage>();
            if (storageProvider == null)
            {
                ThrowMissingProviderException(grain, attr?.ProviderName);
            }

            return storageProvider;
        }

        private static void ThrowMissingProviderException(Grain grain, string name)
        {
            string errMsg;
            var grainTypeName = grain.GetType().GetParseableName(TypeFormattingOptions.LogFormat);
            if (string.IsNullOrEmpty(name))
            {
                errMsg = $"No default storage provider found loading grain type {grainTypeName}.";
            }
            else
            {
                errMsg = $"No storage provider named \"{name}\" found loading grain type {grainTypeName}.";
            }

            throw new BadGrainStorageConfigException(errMsg);
        }
    }
}
