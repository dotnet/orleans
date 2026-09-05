using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Clustering.DynamoDB;
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
        builder.UseDynamoDBClustering((OptionsBuilder<DynamoDBClusteringOptions> optionsBuilder) =>
            optionsBuilder.Configure<IConfiguration>((options, configuration) =>
            {
                var providerConfiguration = DynamoDBProviderConfiguration.Create(configurationSection, configuration);
                providerConfiguration.ConfigureClientOptions(options);

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
            }));
    }

    public void Configure(IClientBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.UseDynamoDBClustering((OptionsBuilder<DynamoDBGatewayOptions> optionsBuilder) =>
            optionsBuilder.Configure<IConfiguration>((options, configuration) =>
            {
                var providerConfiguration = DynamoDBProviderConfiguration.Create(configurationSection, configuration);
                providerConfiguration.ConfigureClientOptions(options);

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
            }));
    }
}
