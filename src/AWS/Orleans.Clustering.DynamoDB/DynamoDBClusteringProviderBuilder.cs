using System;
using Microsoft.Extensions.Configuration;
using Orleans;
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

                options.ReadCapacityUnits = configurationSection.GetValue(nameof(options.ReadCapacityUnits), options.ReadCapacityUnits);
                options.WriteCapacityUnits = configurationSection.GetValue(nameof(options.WriteCapacityUnits), options.WriteCapacityUnits);
                options.UseProvisionedThroughput = configurationSection.GetValue(nameof(options.UseProvisionedThroughput), options.UseProvisionedThroughput);
                options.CreateIfNotExists = configurationSection.GetValue(nameof(options.CreateIfNotExists), options.CreateIfNotExists);
                options.UpdateIfExists = configurationSection.GetValue(nameof(options.UpdateIfExists), options.UpdateIfExists);
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

                options.ReadCapacityUnits = configurationSection.GetValue(nameof(options.ReadCapacityUnits), options.ReadCapacityUnits);
                options.WriteCapacityUnits = configurationSection.GetValue(nameof(options.WriteCapacityUnits), options.WriteCapacityUnits);
                options.UseProvisionedThroughput = configurationSection.GetValue(nameof(options.UseProvisionedThroughput), options.UseProvisionedThroughput);
                options.CreateIfNotExists = configurationSection.GetValue(nameof(options.CreateIfNotExists), options.CreateIfNotExists);
                options.UpdateIfExists = configurationSection.GetValue(nameof(options.UpdateIfExists), options.UpdateIfExists);
            });
    }
}
