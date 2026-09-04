using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Streaming.AdoNet.Storage;

[assembly: RegisterProvider("AdoNet", "Streaming", "Silo", typeof(AdoNetStreamingProviderBuilder))]
[assembly: RegisterProvider("AdoNet", "Streaming", "Client", typeof(AdoNetStreamingProviderBuilder))]
[assembly: RegisterProvider("SqlServerDatabase", "Streaming", "Silo", typeof(AdoNetStreamingProviderBuilder))]
[assembly: RegisterProvider("SqlServerDatabase", "Streaming", "Client", typeof(AdoNetStreamingProviderBuilder))]
[assembly: RegisterProvider("AzureSqlDatabase", "Streaming", "Silo", typeof(AdoNetStreamingProviderBuilder))]
[assembly: RegisterProvider("AzureSqlDatabase", "Streaming", "Client", typeof(AdoNetStreamingProviderBuilder))]
[assembly: RegisterProvider("PostgresDatabase", "Streaming", "Silo", typeof(AdoNetStreamingProviderBuilder))]
[assembly: RegisterProvider("PostgresDatabase", "Streaming", "Client", typeof(AdoNetStreamingProviderBuilder))]
[assembly: RegisterProvider("AzurePostgresFlexibleServerDatabase", "Streaming", "Silo", typeof(AdoNetStreamingProviderBuilder))]
[assembly: RegisterProvider("AzurePostgresFlexibleServerDatabase", "Streaming", "Client", typeof(AdoNetStreamingProviderBuilder))]
[assembly: RegisterProvider("MySqlDatabase", "Streaming", "Silo", typeof(AdoNetStreamingProviderBuilder))]
[assembly: RegisterProvider("MySqlDatabase", "Streaming", "Client", typeof(AdoNetStreamingProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class AdoNetStreamingProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        ArgumentNullException.ThrowIfNull(name);

        builder.AddAdoNetStreams(name, configurator =>
        {
            configurator.ConfigureAdoNet(GetOptionsBuilder(configurationSection));
            ConfigurePartitioning(configurator, configurationSection);
        });
    }

    public void Configure(IClientBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        ArgumentNullException.ThrowIfNull(name);

        builder.AddAdoNetStreams(name, configurator =>
        {
            configurator.ConfigureAdoNet(GetOptionsBuilder(configurationSection));
            ConfigurePartitioning(configurator, configurationSection);
        });
    }

    private static Action<OptionsBuilder<AdoNetStreamOptions>> GetOptionsBuilder(IConfigurationSection configurationSection)
        => optionsBuilder =>
        {
            optionsBuilder.Bind(configurationSection);
            optionsBuilder.Configure<IServiceProvider>((options, services) =>
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
            });
        };

    private static void ConfigurePartitioning(SiloAdoNetStreamConfigurator configurator, IConfigurationSection configurationSection)
    {
        if (GetPartitionCount(configurationSection) is { } partitionCount)
        {
            configurator.ConfigurePartitioning(partitionCount);
        }
    }

    private static void ConfigurePartitioning(ClusterClientAdoNetStreamConfigurator configurator, IConfigurationSection configurationSection)
    {
        if (GetPartitionCount(configurationSection) is { } partitionCount)
        {
            configurator.ConfigurePartitioning(partitionCount);
        }
    }

    private static int? GetPartitionCount(IConfigurationSection configurationSection)
    {
        var value = configurationSection["PartitionCount"];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value, out var partitionCount) || partitionCount <= 0)
        {
            throw new OrleansConfigurationException(
                $"ADO.NET streaming configuration section '{configurationSection.Path}' setting 'PartitionCount' must be a positive integer.");
        }

        return partitionCount;
    }
}
