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
            }));
    }
}
