using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Reminders.DynamoDB;

[assembly: RegisterProvider("DynamoDB", "Reminders", "Silo", typeof(DynamoDBRemindersProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class DynamoDBRemindersProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.ConfigureServices(services =>
        {
            services.UseDynamoDBReminderService(_ => { });
            services.AddOptions<DynamoDBReminderStorageOptions>()
                .Configure<IConfiguration>((options, configuration) =>
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
            });
        });
    }
}
