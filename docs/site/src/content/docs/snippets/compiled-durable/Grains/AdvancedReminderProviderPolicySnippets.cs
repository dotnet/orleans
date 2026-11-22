using Orleans.AdvancedReminders.DynamoDB;
using Orleans.AdvancedReminders.Redis;
using Orleans.Hosting;
using StackExchange.Redis;

namespace Documentation.Grains.AdvancedScheduling;

internal static class AdvancedReminderProviderPolicySnippets
{
    internal static void ConfigureRedis(ISiloBuilder siloBuilder)
    {
        // <configure_advanced_reminder_redis_expiry>
        siloBuilder.UseRedisAdvancedReminderService(options =>
        {
            options.ConfigurationOptions = ConfigurationOptions.Parse("localhost:6379");
            options.TableExpiry = ReminderTableExpiry.Never;
        });
        // </configure_advanced_reminder_redis_expiry>
    }

    internal static void ConfigureEphemeralRedis(ISiloBuilder siloBuilder)
    {
        // <configure_advanced_reminder_redis_inactivity_expiry>
        siloBuilder.UseRedisAdvancedReminderService(options =>
        {
            options.ConfigurationOptions = ConfigurationOptions.Parse("localhost:6379");
            options.TableExpiry = ReminderTableExpiry.AfterInactivity(TimeSpan.FromHours(1));
        });
        // </configure_advanced_reminder_redis_inactivity_expiry>
    }

    internal static void ConfigureOnDemandDynamoDB(ISiloBuilder siloBuilder)
    {
        // <configure_advanced_reminder_dynamodb_on_demand>
        siloBuilder.UseDynamoDBAdvancedReminderService(options =>
        {
            options.Service = "us-east-1";
            options.TableName = "OrleansAdvancedReminders";
            options.Capacity = DynamoDBCapacity.OnDemand;
        });
        // </configure_advanced_reminder_dynamodb_on_demand>
    }

    internal static void ConfigureProvisionedDynamoDB(ISiloBuilder siloBuilder)
    {
        // <configure_advanced_reminder_dynamodb_provisioned>
        siloBuilder.UseDynamoDBAdvancedReminderService(options =>
        {
            options.Service = "us-east-1";
            options.TableName = "OrleansAdvancedReminders";
            options.Capacity = DynamoDBCapacity.Provisioned(readUnits: 10, writeUnits: 5);
        });
        // </configure_advanced_reminder_dynamodb_provisioned>
    }
}
