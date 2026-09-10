using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.AdvancedReminders.DynamoDB;
using Xunit;
using ClusterOptions = Orleans.Configuration.ClusterOptions;

namespace AWSUtils.Tests.AdvancedReminders;

[TestSuite("Functional")]
[TestProvider("DynamoDB")]
[TestArea("Reminders")]
public class DynamoDBAdvancedReminderCapacityTests
{
    [Theory]
    [InlineData(null, 10, 5)]
    [InlineData("OnDemand", 0, 0)]
    [InlineData("Provisioned(7,9)", 7, 9)]
    public async Task Startup_AppliesCapacityToTableAndBothIndexes(string? configured, int readUnits, int writeUnits)
    {
        if (!Uri.TryCreate(AWSTestConstants.DynamoDbService, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback)
        {
            throw Xunit.Sdk.SkipException.ForSkip("This test requires a local DynamoDB emulator.");
        }

        using var client = new AmazonDynamoDBClient(new BasicAWSCredentials("dummy", "dummyKey"),
            new AmazonDynamoDBConfig { ServiceURL = endpoint.AbsoluteUri });
        var options = new DynamoDBReminderStorageOptions
        {
            Service = endpoint.AbsoluteUri,
            TableName = "AdvancedReminderCapacity" + Guid.NewGuid().ToString("N"),
        };
        if (configured is not null)
        {
            options.Capacity = DynamoDBCapacity.Parse(configured);
        }

        Orleans.AdvancedReminders.IReminderTable table = new DynamoDBReminderTable(
            NullLoggerFactory.Instance,
            Options.Create(new ClusterOptions { ServiceId = Guid.NewGuid().ToString("N") }),
            Options.Create(options));
        try
        {
            await table.StartAsync(TestContext.Current.CancellationToken);
            var response = await client.DescribeTableAsync(options.TableName, TestContext.Current.CancellationToken);
            var description = response.Table;

            Assert.Equal(readUnits == 0 ? BillingMode.PAY_PER_REQUEST : BillingMode.PROVISIONED,
                description.BillingModeSummary?.BillingMode ?? BillingMode.PROVISIONED);
            Assert.Equal(readUnits, description.ProvisionedThroughput?.ReadCapacityUnits ?? 0);
            Assert.Equal(writeUnits, description.ProvisionedThroughput?.WriteCapacityUnits ?? 0);
            Assert.Equal(2, description.GlobalSecondaryIndexes.Count);
            Assert.All(description.GlobalSecondaryIndexes, index =>
            {
                Assert.Equal(readUnits, index.ProvisionedThroughput?.ReadCapacityUnits ?? 0);
                Assert.Equal(writeUnits, index.ProvisionedThroughput?.WriteCapacityUnits ?? 0);
            });
        }
        finally
        {
            await table.StopAsync(CancellationToken.None);
            try
            {
                await client.DeleteTableAsync(options.TableName, CancellationToken.None);
            }
            catch (ResourceNotFoundException)
            {
                // Startup can fail before the test table has been created.
            }
        }
    }
}
