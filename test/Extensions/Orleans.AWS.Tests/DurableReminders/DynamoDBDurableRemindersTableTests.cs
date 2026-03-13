extern alias DurableRemindersDynamoDB;

#nullable enable
using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestExtensions;
using UnitTests;
using UnitTests.DurableRemindersTest;
using Xunit;
using ClusterOptions = Orleans.Configuration.ClusterOptions;
using LoggerFilterOptions = Microsoft.Extensions.Logging.LoggerFilterOptions;
using DynamoDBReminderTable = DurableRemindersDynamoDB::Orleans.DurableReminders.DynamoDB.DynamoDBReminderTable;
using DynamoDBReminderStorageOptions = DurableRemindersDynamoDB::Orleans.Configuration.DynamoDBReminderStorageOptions;
using DynamoDBReminderStorageOptionsExtensions = DurableRemindersDynamoDB::Orleans.Configuration.DynamoDBReminderStorageOptionsExtensions;

namespace AWSUtils.Tests.DurableReminders;

[TestCategory("Reminders"), TestCategory("AWS"), TestCategory("DynamoDb")]
[Collection(TestEnvironmentFixture.DefaultCollection)]
public class DynamoDBDurableRemindersTableTests : DurableReminderTableTestsBase, IClassFixture<DynamoDBStorageTestsFixture>
{
    public DynamoDBDurableRemindersTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment)
        : base(fixture, environment, new LoggerFilterOptions())
    {
    }

    protected override Orleans.DurableReminders.IReminderTable CreateRemindersTable()
    {
        if (!AWSTestConstants.IsDynamoDbAvailable)
        {
            throw new SkipException("Unable to connect to AWS DynamoDB simulator");
        }

        var options = new DynamoDBReminderStorageOptions();
        DynamoDBReminderStorageOptionsExtensions.ParseConnectionString(options, connectionStringFixture.ConnectionString);

        return new DynamoDBReminderTable(
            loggerFactory,
            clusterOptions,
            Options.Create(options));
    }

    protected override Task<string> GetConnectionString()
        => Task.FromResult(AWSTestConstants.IsDynamoDbAvailable ? $"Service={AWSTestConstants.DynamoDbService}" : null!);

    [SkippableFact]
    public async Task RemindersTable_AWS_DurableCronRoundTrip() => await ReminderCronRoundTrip();

    [SkippableFact]
    public async Task RemindersTable_AWS_DurableAdaptiveFieldsRoundTrip() => await ReminderAdaptiveFieldsRoundTrip();

    [SkippableFact]
    public async Task RemindersTable_AWS_DurableCronTimeZoneRoundTrip() => await ReminderCronTimeZoneRoundTrip();
}
