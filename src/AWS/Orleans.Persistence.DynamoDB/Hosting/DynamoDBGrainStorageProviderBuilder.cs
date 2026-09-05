using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Persistence.DynamoDB;
using Orleans.Providers;
using Orleans.Storage;

[assembly: RegisterProvider("DynamoDB", "GrainStorage", "Silo", typeof(DynamoDBGrainStorageProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class DynamoDBGrainStorageProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        var configuration = builder.Configuration;
        builder.AddDynamoDBGrainStorage(
            name!,
            (OptionsBuilder<DynamoDBStorageOptions> optionsBuilder) => optionsBuilder.Configure<IServiceProvider>((options, services) =>
            {
                var providerConfiguration = DynamoDBProviderConfiguration.Create(configurationSection, configuration);
                providerConfiguration.ConfigureClientOptions(options);

                var serviceId = providerConfiguration.GetValue(nameof(options.ServiceId));
                if (serviceId is not null)
                {
                    options.ServiceId = serviceId;
                }

                var tableName = providerConfiguration.GetValue(nameof(options.TableName));
                if (tableName is not null)
                {
                    options.TableName = tableName;
                }

                if (providerConfiguration.GetInt32(nameof(options.ReadCapacityUnits)) is { } rcu)
                {
                    options.ReadCapacityUnits = rcu;
                }

                if (providerConfiguration.GetInt32(nameof(options.WriteCapacityUnits)) is { } wcu)
                {
                    options.WriteCapacityUnits = wcu;
                }

                if (providerConfiguration.GetBoolean(nameof(options.UseProvisionedThroughput)) is { } upt)
                {
                    options.UseProvisionedThroughput = upt;
                }

                if (providerConfiguration.GetBoolean(nameof(options.CreateIfNotExists)) is { } cine)
                {
                    options.CreateIfNotExists = cine;
                }

                if (providerConfiguration.GetBoolean(nameof(options.UpdateIfExists)) is { } uie)
                {
                    options.UpdateIfExists = uie;
                }

                if (providerConfiguration.GetBoolean(nameof(options.DeleteStateOnClear)) is { } dsoc)
                {
                    options.DeleteStateOnClear = dsoc;
                }

                if (providerConfiguration.GetTimeSpan(nameof(options.TimeToLive)) is { } ttl)
                {
                    options.TimeToLive = ttl;
                }

                var serializerKey = configurationSection["SerializerKey"];
                if (!string.IsNullOrEmpty(serializerKey))
                {
                    options.GrainStorageSerializer = services.GetRequiredKeyedService<IGrainStorageSerializer>(serializerKey);
                }
            }));
    }
}
