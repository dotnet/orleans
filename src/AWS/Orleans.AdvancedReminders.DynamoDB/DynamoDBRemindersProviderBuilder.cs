using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.AdvancedReminders.DynamoDB;

[assembly: RegisterProvider("DynamoDB", "AdvancedReminders", "Silo", typeof(AdvancedDynamoDBRemindersProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class AdvancedDynamoDBRemindersProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.UseDynamoDBAdvancedReminderService(options =>
            {
                foreach (var child in configurationSection.GetChildren())
                {
                    DynamoDBReminderStorageOptionsExtensions.ThrowIfLegacyCapacityOption(child.Key);
                    if (child.Key.Equals(nameof(options.Capacity), StringComparison.OrdinalIgnoreCase))
                    {
                        var capacity = child.Value;
                        if (string.IsNullOrWhiteSpace(capacity))
                        {
                            throw new OrleansConfigurationException("Capacity requires a named capacity policy.");
                        }

                        options.Capacity = DynamoDBCapacity.Parse(capacity);
                    }
                }

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

                if (bool.TryParse(configurationSection[nameof(options.CreateIfNotExists)], out var cine))
                {
                    options.CreateIfNotExists = cine;
                }

                if (bool.TryParse(configurationSection[nameof(options.UpdateIfExists)], out var uie))
                {
                    options.UpdateIfExists = uie;
                }
            });
    }
}
