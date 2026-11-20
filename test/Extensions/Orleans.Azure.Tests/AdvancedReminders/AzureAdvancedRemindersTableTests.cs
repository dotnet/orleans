#nullable enable
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.AdvancedReminders.AzureStorage;
using Orleans.Runtime;
using Tester;
using Tester.AzureUtils;
using TestExtensions;
using Xunit;

namespace UnitTests.AdvancedRemindersTest;

[TestSuite("Functional")]
[TestProvider("AzureStorage")]
[TestArea("Reminders")]
[TestCategory("Reminders"), TestCategory("AzureStorage")]
public class AzureAdvancedRemindersTableTests : AdvancedReminderTableTestsBase
{
    public AzureAdvancedRemindersTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment)
        : base(fixture, environment, CreateFilters())
    {
        TestUtils.CheckForAzureStorage();
    }

    private static LoggerFilterOptions CreateFilters()
    {
        var filters = new LoggerFilterOptions();
        filters.AddFilter("AzureTableDataManager", LogLevel.Trace);
        filters.AddFilter("OrleansSiloInstanceManager", LogLevel.Trace);
        filters.AddFilter("Storage", LogLevel.Trace);
        return filters;
    }

    protected override Orleans.AdvancedReminders.IReminderTable CreateRemindersTable()
    {
        TestUtils.CheckForAzureStorage();
        var options = Options.Create(new AzureTableReminderStorageOptions());
        options.Value.TableServiceClient = AzureStorageOperationOptionsExtensions.GetTableServiceClient();
        return new AzureBasedReminderTable(loggerFactory, clusterOptions, options);
    }

    protected override Task<string> GetConnectionString()
    {
        TestUtils.CheckForAzureStorage();
        return Task.FromResult("not used");
    }

    [Fact]
    public async Task RemindersTable_Azure_PreciseStartTimeRoundTrip()
    {
        var startAt = new DateTime(2026, 9, 7, 12, 0, 0, 123, DateTimeKind.Utc).AddTicks(4_567);
        var reminder = new Orleans.AdvancedReminders.ReminderEntry
        {
            GrainId = GrainId.Create("test", "precise-start-time"),
            ReminderName = "precise-start-time",
            StartAt = startAt,
            NextDueUtc = startAt.AddMinutes(5),
            Period = TimeSpan.FromMinutes(1),
        };

        var etag = await remindersTable.UpsertRow(reminder, TestContext.Current.CancellationToken);

        var pointRead = await remindersTable.ReadRow(reminder.GrainId, reminder.ReminderName);
        Assert.NotNull(pointRead);
        Assert.Equal(startAt, pointRead.StartAt);
        Assert.Equal(DateTimeKind.Utc, pointRead.StartAt.Kind);
        Assert.Equal(reminder.NextDueUtc, pointRead.NextDueUtc);
        Assert.Equal(etag, pointRead.ETag);

        var page = await remindersTable.ReadRows(0, 0, 1, continuationToken: null);
        var pagedRead = Assert.Single(page.Reminders);
        Assert.Equal(startAt, pagedRead.StartAt);
        Assert.Equal(DateTimeKind.Utc, pagedRead.StartAt.Kind);
    }

    [Fact]
    public async Task RemindersTable_Azure_PreservesStartTimeAcrossMixedVersionWrites()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var startAt = new DateTime(2026, 9, 7, 12, 0, 0, 123, DateTimeKind.Utc);
        var reminder = new Orleans.AdvancedReminders.ReminderEntry
        {
            GrainId = GrainId.Create("test", "mixed-version-start-time"),
            ReminderName = "mixed-version-start-time",
            StartAt = startAt.AddTicks(4_567),
            Period = TimeSpan.FromMinutes(1),
        };
        var client = AzureStorageOperationOptionsExtensions.GetTableServiceClient()
            .GetTableClient(AzureTableReminderStorageOptions.DEFAULT_TABLE_NAME);
        var partitionKey = ReminderTableEntry.ConstructPartitionKey(clusterOptions.Value.ServiceId, reminder.GrainId);
        var rowKey = ReminderTableEntry.ConstructRowKey(reminder.GrainId, reminder.ReminderName);

        await remindersTable.UpsertRow(reminder, TestContext.Current.CancellationToken);
        var row = (await client.GetEntityAsync<TableEntity>(partitionKey, rowKey, cancellationToken: cancellationToken)).Value;
        Assert.Equal("2026-09-07T12:00:00.1234567Z", row.GetString(nameof(ReminderTableEntry.StartAtUtc)));
        Assert.Equal(startAt, LogFormatter.ParseDate(row.GetString(nameof(ReminderTableEntry.StartAt))!));

        // An older silo replaces the whole row using the original schema.
        var legacyStartAt = startAt.AddMinutes(2);
        row.Remove(nameof(ReminderTableEntry.StartAtUtc));
        row[nameof(ReminderTableEntry.StartAt)] = LogFormatter.PrintDate(legacyStartAt);
        await client.UpdateEntityAsync(row, row.ETag, TableUpdateMode.Replace, cancellationToken);

        var legacyRead = await remindersTable.ReadRow(reminder.GrainId, reminder.ReminderName);
        Assert.NotNull(legacyRead);
        Assert.Equal(legacyStartAt, legacyRead.StartAt);
        Assert.Equal(DateTimeKind.Utc, legacyRead.StartAt.Kind);

        legacyRead.StartAt = legacyStartAt.AddTicks(7_654);
        await remindersTable.UpsertRow(legacyRead, TestContext.Current.CancellationToken);
        var updated = Assert.Single((await remindersTable.ReadRows(reminder.GrainId)).Reminders);
        Assert.Equal(legacyRead.StartAt, updated.StartAt);
        Assert.Equal(DateTimeKind.Utc, updated.StartAt.Kind);
    }

    [Fact]
    public async Task RemindersTable_Azure_DurableCronRoundTrip() => await ReminderCronRoundTrip();

    [Fact]
    public async Task RemindersTable_Azure_DurableAdaptiveFieldsRoundTrip() => await ReminderAdaptiveFieldsRoundTrip();

    [Fact]
    public async Task RemindersTable_Azure_DurableCronTimeZoneRoundTrip() => await ReminderCronTimeZoneRoundTrip();
}
