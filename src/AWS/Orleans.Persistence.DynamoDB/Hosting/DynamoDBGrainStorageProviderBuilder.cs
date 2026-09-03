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

                if (int.TryParse(configurationSection[nameof(options.ReadCapacityUnits)], out var rcu))
                {
                    options.ReadCapacityUnits = rcu;
                }

                if (int.TryParse(configurationSection[nameof(options.WriteCapacityUnits)], out var wcu))
                {
                    options.WriteCapacityUnits = wcu;
                }

                if (bool.TryParse(configurationSection[nameof(options.UseProvisionedThroughput)], out var upt))
                {
                    options.UseProvisionedThroughput = upt;
                }

                if (bool.TryParse(configurationSection[nameof(options.CreateIfNotExists)], out var cine))
                {
                    options.CreateIfNotExists = cine;
                }

                if (bool.TryParse(configurationSection[nameof(options.UpdateIfExists)], out var uie))
                {
                    options.UpdateIfExists = uie;
                }

                if (bool.TryParse(configurationSection[nameof(options.DeleteStateOnClear)], out var dsoc))
                {
                    options.DeleteStateOnClear = dsoc;
                }

                if (TimeSpan.TryParse(configurationSection[nameof(options.TimeToLive)], out var ttl))
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
