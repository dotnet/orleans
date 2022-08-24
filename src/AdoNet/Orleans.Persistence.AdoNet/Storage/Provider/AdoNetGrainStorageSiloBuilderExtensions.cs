using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;

namespace Orleans.Hosting
{
    public static class AdoNetGrainStorageSiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use AdoNet grain storage as the default grain storage. Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </summary>
        /// <remarks>
        /// Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </remarks>
        public static ISiloBuilder AddAdoNetGrainStorageAsDefault(this ISiloBuilder builder, Action<AdoNetGrainStorageOptions> configureOptions)
        {
            return builder.AddAdoNetGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use  AdoNet grain storage for grain storage. Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </summary>
        /// <remarks>
        /// Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </remarks>
        public static ISiloBuilder AddAdoNetGrainStorage(this ISiloBuilder builder, string name, Action<AdoNetGrainStorageOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddAdoNetGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use  AdoNet grain storage as the default grain storage. Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </summary>
        /// <remarks>
        /// Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </remarks>
        public static ISiloBuilder AddAdoNetGrainStorageAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<AdoNetGrainStorageOptions>> configureOptions = null)
        {
            return builder.AddAdoNetGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use AdoNet grain storage for grain storage. Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </summary>
        /// <remarks>
        /// Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </remarks>
        public static ISiloBuilder AddAdoNetGrainStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<AdoNetGrainStorageOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddAdoNetGrainStorage(name, configureOptions));
        }
    }
}
