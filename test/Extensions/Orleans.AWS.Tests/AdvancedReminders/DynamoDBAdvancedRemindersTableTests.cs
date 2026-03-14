extern alias AdvancedRemindersDynamoDB;

#nullable enable
using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestExtensions;
using UnitTests;
using UnitTests.AdvancedRemindersTest;
using Xunit;
using ClusterOptions = Orleans.Configuration.ClusterOptions;
using LoggerFilterOptions = Microsoft.Extensions.Logging.LoggerFilterOptions;
using DynamoDBReminderTable = AdvancedRemindersDynamoDB::Orleans.AdvancedReminders.DynamoDB.DynamoDBReminderTable;
using DynamoDBReminderStorageOptions = AdvancedRemindersDynamoDB::Orleans.Configuration.DynamoDBReminderStorageOptions;
using DynamoDBReminderStorageOptionsExtensions = AdvancedRemindersDynamoDB::Orleans.Configuration.DynamoDBReminderStorageOptionsExtensions;

namespace AWSUtils.Tests.AdvancedReminders;

[TestCategory("Reminders"), TestCategory("AWS"), TestCategory("DynamoDb")]
[Collection(TestEnvironmentFixture.DefaultCollection)]
public class DynamoDBAdvancedRemindersTableTests : AdvancedReminderTableTestsBase, IClassFixture<DynamoDBStorageTestsFixture>
{
    public DynamoDBAdvancedRemindersTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment)
        : base(fixture, environment, new LoggerFilterOptions())
    {
    }

    protected override Orleans.AdvancedReminders.IReminderTable CreateRemindersTable()
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
