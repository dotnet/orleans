using System;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;

[assembly: RegisterProvider("DynamoDB", "Clustering", "Silo", typeof(DynamoDBClusteringProviderBuilder))]
[assembly: RegisterProvider("DynamoDB", "Clustering", "Client", typeof(DynamoDBClusteringProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class DynamoDBClusteringProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.UseDynamoDBClustering(options =>
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
            });
    }

    public void Configure(IClientBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.UseDynamoDBClustering(options =>
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
            });
    }
}
