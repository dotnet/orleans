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

                var serviceId = configurationSection[nameof(options.ServiceId)];
                if (!string.IsNullOrEmpty(serviceId))
                {
                    options.ServiceId = serviceId;
                }

                var tableName = configurationSection[nameof(options.TableName)];
                if (!string.IsNullOrEmpty(tableName))
                {
                    options.TableName = tableName;
                }

                if (ConfigurationValueParser.TryParseValue<int>(configurationSection, nameof(options.ReadCapacityUnits), out var rcu))
                {
                    options.ReadCapacityUnits = rcu;
                }

                if (ConfigurationValueParser.TryParseValue<int>(configurationSection, nameof(options.WriteCapacityUnits), out var wcu))
                {
                    options.WriteCapacityUnits = wcu;
                }

                if (ConfigurationValueParser.TryParseValue<bool>(configurationSection, nameof(options.UseProvisionedThroughput), out var upt))
                {
                    options.UseProvisionedThroughput = upt;
                }

                if (ConfigurationValueParser.TryParseValue<bool>(configurationSection, nameof(options.CreateIfNotExists), out var cine))
                {
                    options.CreateIfNotExists = cine;
                }

                if (ConfigurationValueParser.TryParseValue<bool>(configurationSection, nameof(options.UpdateIfExists), out var uie))
                {
                    options.UpdateIfExists = uie;
                }

                if (ConfigurationValueParser.TryParseValue<bool>(configurationSection, nameof(options.DeleteStateOnClear), out var dsoc))
                {
                    options.DeleteStateOnClear = dsoc;
                }

                if (ConfigurationValueParser.TryParseValue<TimeSpan>(configurationSection, nameof(options.TimeToLive), out var ttl))
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
