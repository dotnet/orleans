using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Persistence.AdoNet.Storage;
using Orleans.Providers;
using Orleans.Storage;

[assembly: RegisterProvider("AdoNet", "GrainStorage", "Silo", typeof(AdoNetGrainStorageProviderBuilder))]
[assembly: RegisterProvider("SqlServerDatabase", "GrainStorage", "Silo", typeof(AdoNetGrainStorageProviderBuilder))]
[assembly: RegisterProvider("AzureSqlDatabase", "GrainStorage", "Silo", typeof(AdoNetGrainStorageProviderBuilder))]
[assembly: RegisterProvider("PostgresDatabase", "GrainStorage", "Silo", typeof(AdoNetGrainStorageProviderBuilder))]
[assembly: RegisterProvider("AzurePostgresFlexibleServerDatabase", "GrainStorage", "Silo", typeof(AdoNetGrainStorageProviderBuilder))]
[assembly: RegisterProvider("MySqlDatabase", "GrainStorage", "Silo", typeof(AdoNetGrainStorageProviderBuilder))]
[assembly: RegisterProvider("OracleDatabase", "GrainStorage", "Silo", typeof(AdoNetGrainStorageProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class AdoNetGrainStorageProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.AddAdoNetGrainStorage(name!, (OptionsBuilder<AdoNetGrainStorageOptions> optionsBuilder) => optionsBuilder.Configure<IServiceProvider>((options, services) =>
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

                var serializerKey = configurationSection["SerializerKey"];
                if (!string.IsNullOrEmpty(serializerKey))
                {
                    options.GrainStorageSerializer = services.GetRequiredKeyedService<IGrainStorageSerializer>(serializerKey);
                }
            }));
    }
}
