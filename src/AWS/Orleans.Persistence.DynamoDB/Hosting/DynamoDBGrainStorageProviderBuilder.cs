using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Storage;

[assembly: RegisterProvider("DynamoDB", "GrainStorage", "Silo", typeof(DynamoDBGrainStorageProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class DynamoDBGrainStorageProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.AddDynamoDBGrainStorage(
            name!,
            (OptionsBuilder<DynamoDBStorageOptions> optionsBuilder) => optionsBuilder.Configure<IServiceProvider>((options, services) =>
            {
                var accessKey = configurationSection[nameof(options.AccessKey)];
                if (!string.IsNullOrEmpty(accessKey))
                {
                    options.AccessKey = accessKey;
                }

                var secretKey = configurationSection[nameof(options.SecretKey)];
                if (!string.IsNullOrEmpty(secretKey))
                {
                    options.SecretKey = secretKey;
                }

                var region = configurationSection[nameof(options.Service)] ?? configurationSection["Region"];
                if (!string.IsNullOrEmpty(region))
                {
                    options.Service = region;
                }

                var token = configurationSection[nameof(options.Token)];
                if (!string.IsNullOrEmpty(token))
                {
                    options.Token = token;
                }

                var profileName = configurationSection[nameof(options.ProfileName)];
                if (!string.IsNullOrEmpty(profileName))
                {
                    options.ProfileName = profileName;
                }

                // An empty value is kept: it selects the keys written while ServiceId defaulted to empty.
                var serviceId = configurationSection[nameof(options.ServiceId)];
                if (serviceId is not null)
                {
                    options.ServiceId = serviceId;
                }

                var tableName = configurationSection[nameof(options.TableName)];
                if (!string.IsNullOrEmpty(tableName))
                {
                    options.TableName = tableName;
                }

                options.ReadCapacityUnits = configurationSection.GetValue(nameof(options.ReadCapacityUnits), options.ReadCapacityUnits);
                options.WriteCapacityUnits = configurationSection.GetValue(nameof(options.WriteCapacityUnits), options.WriteCapacityUnits);
                options.UseProvisionedThroughput = configurationSection.GetValue(nameof(options.UseProvisionedThroughput), options.UseProvisionedThroughput);
                options.CreateIfNotExists = configurationSection.GetValue(nameof(options.CreateIfNotExists), options.CreateIfNotExists);
                options.UpdateIfExists = configurationSection.GetValue(nameof(options.UpdateIfExists), options.UpdateIfExists);
                options.DeleteStateOnClear = configurationSection.GetValue(nameof(options.DeleteStateOnClear), options.DeleteStateOnClear);

                if (configurationSection.GetValue<TimeSpan?>(nameof(options.TimeToLive)) is { } ttl)
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
