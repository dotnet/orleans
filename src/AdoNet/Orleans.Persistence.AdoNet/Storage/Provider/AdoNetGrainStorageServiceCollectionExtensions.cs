using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Hosting;
using Orleans.Storage;

namespace Orleans.Hosting
{
    /// <summary>
    /// Provides extension methods for registering ADO.NET grain storage with a service collection.
    /// </summary>
    public static class AdoNetGrainStorageServiceCollectionExtensions
    {
        /// <summary>
        /// Adds ADO.NET grain storage as the default grain storage provider.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configureOptions">The delegate used to configure the storage provider.</param>
        /// <returns>The provided <see cref="IServiceCollection"/>, for chaining.</returns>
        /// <remarks>
        /// Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </remarks>
        public static IServiceCollection AddAdoNetGrainStorage(this IServiceCollection services, Action<AdoNetGrainStorageOptions> configureOptions)
        {
            return services.AddAdoNetGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Adds a named ADO.NET grain storage provider.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="name">The storage provider name.</param>
        /// <param name="configureOptions">The delegate used to configure the storage provider.</param>
        /// <returns>The provided <see cref="IServiceCollection"/>, for chaining.</returns>
        /// <remarks>
        /// Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </remarks>
        public static IServiceCollection AddAdoNetGrainStorage(this IServiceCollection services, string name, Action<AdoNetGrainStorageOptions> configureOptions)
        {
            return services.AddAdoNetGrainStorage(name, ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Adds ADO.NET grain storage as the default grain storage provider.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configureOptions">The delegate used to configure the named options builder.</param>
        /// <returns>The provided <see cref="IServiceCollection"/>, for chaining.</returns>
        /// <remarks>
        /// Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </remarks>
        public static IServiceCollection AddAdoNetGrainStorageAsDefault(this IServiceCollection services, Action<OptionsBuilder<AdoNetGrainStorageOptions>>? configureOptions = null)
        {
            return services.AddAdoNetGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Adds a named ADO.NET grain storage provider.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="name">The storage provider name.</param>
        /// <param name="configureOptions">The delegate used to configure the named options builder.</param>
        /// <returns>The provided <see cref="IServiceCollection"/>, for chaining.</returns>
        /// <remarks>
        /// Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </remarks>
        public static IServiceCollection AddAdoNetGrainStorage(this IServiceCollection services, string name,
            Action<OptionsBuilder<AdoNetGrainStorageOptions>>? configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<AdoNetGrainStorageOptions>(name));
            services.ConfigureNamedOptionForLogging<AdoNetGrainStorageOptions>(name);
            services.AddTransient<IPostConfigureOptions<AdoNetGrainStorageOptions>, DefaultStorageProviderSerializerOptionsConfigurator<AdoNetGrainStorageOptions>>();
            services.AddTransient<IPostConfigureOptions<AdoNetGrainStorageOptions>, DefaultAdoNetGrainStorageOptionsHashPickerConfigurator>();
            services.AddTransient<IConfigurationValidator>(sp => new AdoNetGrainStorageOptionsValidator(sp.GetRequiredService<IOptionsMonitor<AdoNetGrainStorageOptions>>().Get(name), name));
            return services.AddGrainStorage(name, AdoNetGrainStorageFactory.Create);
        }
    }
}
