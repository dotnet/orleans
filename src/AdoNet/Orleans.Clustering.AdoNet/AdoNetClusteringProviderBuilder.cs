using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Clustering.AdoNet.Storage;
using Orleans.Hosting;
using Orleans.Providers;

[assembly: RegisterProvider("AdoNet", "Clustering", "Silo", typeof(AdoNetClusteringProviderBuilder))]
[assembly: RegisterProvider("AdoNet", "Clustering", "Client", typeof(AdoNetClusteringProviderBuilder))]
[assembly: RegisterProvider("SqlServerDatabase", "Clustering", "Silo", typeof(AdoNetClusteringProviderBuilder))]
[assembly: RegisterProvider("SqlServerDatabase", "Clustering", "Client", typeof(AdoNetClusteringProviderBuilder))]
[assembly: RegisterProvider("AzureSqlDatabase", "Clustering", "Silo", typeof(AdoNetClusteringProviderBuilder))]
[assembly: RegisterProvider("AzureSqlDatabase", "Clustering", "Client", typeof(AdoNetClusteringProviderBuilder))]
[assembly: RegisterProvider("PostgresDatabase", "Clustering", "Silo", typeof(AdoNetClusteringProviderBuilder))]
[assembly: RegisterProvider("PostgresDatabase", "Clustering", "Client", typeof(AdoNetClusteringProviderBuilder))]
[assembly: RegisterProvider("AzurePostgresFlexibleServerDatabase", "Clustering", "Silo", typeof(AdoNetClusteringProviderBuilder))]
[assembly: RegisterProvider("AzurePostgresFlexibleServerDatabase", "Clustering", "Client", typeof(AdoNetClusteringProviderBuilder))]
[assembly: RegisterProvider("MySqlDatabase", "Clustering", "Silo", typeof(AdoNetClusteringProviderBuilder))]
[assembly: RegisterProvider("MySqlDatabase", "Clustering", "Client", typeof(AdoNetClusteringProviderBuilder))]
[assembly: RegisterProvider("OracleDatabase", "Clustering", "Silo", typeof(AdoNetClusteringProviderBuilder))]
[assembly: RegisterProvider("OracleDatabase", "Clustering", "Client", typeof(AdoNetClusteringProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class AdoNetClusteringProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.UseAdoNetClustering((OptionsBuilder<AdoNetClusteringSiloOptions> optionsBuilder) => optionsBuilder.Configure<IServiceProvider>((options, services) =>
            {
                var invariant = AdoNetProviderConfiguration.GetInvariant(configurationSection);
                if (!string.IsNullOrWhiteSpace(invariant))
                {
                    options.Invariant = invariant;
                }

                var connectionString = AdoNetProviderConfiguration.GetConnectionString(configurationSection, services);
                if (!string.IsNullOrWhiteSpace(connectionString))
                {
                    options.ConnectionString = connectionString;
                }
            }));
    }

    public void Configure(IClientBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.UseAdoNetClustering((OptionsBuilder<AdoNetClusteringClientOptions> optionsBuilder) => optionsBuilder.Configure<IServiceProvider>((options, services) =>
            {
                var invariant = AdoNetProviderConfiguration.GetInvariant(configurationSection);
                if (!string.IsNullOrWhiteSpace(invariant))
                {
                    options.Invariant = invariant;
                }

                var connectionString = AdoNetProviderConfiguration.GetConnectionString(configurationSection, services);
                if (!string.IsNullOrWhiteSpace(connectionString))
                {
                    options.ConnectionString = connectionString;
                }
            }));
    }
}
