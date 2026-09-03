using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;

namespace Orleans.Hosting
{
    /// <summary>
    /// Provides extension methods for configuring ADO.NET grain storage on an Orleans silo.
    /// </summary>
    public static class AdoNetGrainStorageSiloBuilderExtensions
    {
        /// <summary>
        /// Adds ADO.NET grain storage as the default grain storage provider.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="configureOptions">The delegate used to configure the storage provider.</param>
        /// <returns>The provided <see cref="ISiloBuilder"/>, for chaining.</returns>
        /// <remarks>
        /// Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </remarks>
        public static ISiloBuilder AddAdoNetGrainStorageAsDefault(this ISiloBuilder builder, Action<AdoNetGrainStorageOptions> configureOptions)
        {
            return builder.AddAdoNetGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Adds a named ADO.NET grain storage provider.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The storage provider name.</param>
        /// <param name="configureOptions">The delegate used to configure the storage provider.</param>
        /// <returns>The provided <see cref="ISiloBuilder"/>, for chaining.</returns>
        /// <remarks>
        /// Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </remarks>
        public static ISiloBuilder AddAdoNetGrainStorage(this ISiloBuilder builder, string name, Action<AdoNetGrainStorageOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddAdoNetGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Adds ADO.NET grain storage as the default grain storage provider.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="configureOptions">The delegate used to configure the named options builder.</param>
        /// <returns>The provided <see cref="ISiloBuilder"/>, for chaining.</returns>
        /// <remarks>
        /// Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </remarks>
        public static ISiloBuilder AddAdoNetGrainStorageAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<AdoNetGrainStorageOptions>>? configureOptions = null)
        {
            return builder.AddAdoNetGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Adds a named ADO.NET grain storage provider.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The storage provider name.</param>
        /// <param name="configureOptions">The delegate used to configure the named options builder.</param>
        /// <returns>The provided <see cref="ISiloBuilder"/>, for chaining.</returns>
        /// <remarks>
        /// Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </remarks>
        public static ISiloBuilder AddAdoNetGrainStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<AdoNetGrainStorageOptions>>? configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddAdoNetGrainStorage(name, configureOptions));
        }
    }
}
