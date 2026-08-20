using Orleans.Configuration;
using Orleans.Hosting;

namespace Documentation.Grains.Reminders.DynamoDB;

internal static class DynamoDBReminderConfiguration
{
    internal static void Configure(ISiloBuilder siloBuilder)
    {
        const string region = "us-west-2";

        // <configure_dynamodb_reminders>
        siloBuilder
            .Configure<ClusterOptions>(options =>
            {
                options.ClusterId = "production";
                options.ServiceId = "my-application";
            })
            .UseDynamoDBClustering(options =>
            {
                options.Service = region;
                options.TableName = "OrleansCluster";
                options.UseProvisionedThroughput = false;
            })
            .UseDynamoDBReminderService(options =>
            {
                options.Service = region;
                options.TableName = "OrleansReminders";
                options.UseProvisionedThroughput = false;
                options.CreateIfNotExists = false;
            });
        // </configure_dynamodb_reminders>
    }
}
