using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Hosting;
using Orleans.Providers;

[assembly: RegisterProvider("DynamoDB", "Reminders", "Silo", typeof(DynamoDBRemindersProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class DynamoDBRemindersProviderBuilder : IProviderBuilder<ISiloBuilder>
{
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.UseDynamoDBReminderService(options =>
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
