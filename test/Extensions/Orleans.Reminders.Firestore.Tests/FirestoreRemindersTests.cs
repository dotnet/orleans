using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnitTests;
using TestExtensions;
using UnitTests.RemindersTest;
using Orleans.Reminders.Firestore;
using Orleans.Runtime;


namespace Orleans.Reminders.Firestore.Tests;

[TestSuite("Functional")]
[TestProvider("GoogleCloud")]
[TestArea("Reminders")]
[TestCategory("Reminders"), TestCategory("Firestore"), TestCategory("GoogleCloud"), TestCategory("Functional")]
[Collection(TestEnvironmentFixture.DefaultCollection)]
public class FirestoreRemindersTests : ReminderTableTestsBase, IClassFixture<TestEnvironmentFixture>
{
    public FirestoreRemindersTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment)
        : base(fixture, environment, new LoggerFilterOptions())
    {
    }

    protected override IReminderTable CreateRemindersTable()
    {
        var options = new FirestoreOptions
        {
            ProjectId = GoogleEmulatorHost.ProjectId,
            EmulatorHost = GoogleEmulatorHost.FirestoreEndpoint
        };

        return new FirestoreReminderTable(
            this.loggerFactory,
            this.clusterOptions,
            Options.Create(options));
    }

    protected override Task<string> GetConnectionString() => Task.FromResult(GoogleEmulatorHost.FirestoreEndpoint);

    [SkippableFact]
    public async Task ReadsReminderRanges()
    {
        await RemindersRange(50);
    }

    [SkippableFact]
    public async Task SupportsParallelUpserts()
    {
        await RemindersParallelUpsert();
    }

    [SkippableFact]
    public async Task SupportsReminderLifecycle()
    {
        await ReminderSimple();
    }

    [SkippableFact]
    public async Task ReminderIdsDoNotCollide()
    {
        var startAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var period = TimeSpan.FromMinutes(1);
        var first = new ReminderEntry
        {
            GrainId = GrainId.Parse("b__c/d"),
            ReminderName = "a",
            StartAt = startAt,
            Period = period,
        };
        var second = new ReminderEntry
        {
            GrainId = GrainId.Parse("c/d"),
            ReminderName = "a__b",
            StartAt = startAt,
            Period = period,
        };

        await RemindersTable.UpsertRow(first);
        await RemindersTable.UpsertRow(second);

        Assert.Equal(first.ReminderName, (await RemindersTable.ReadRow(first.GrainId, first.ReminderName))?.ReminderName);
        Assert.Equal(second.ReminderName, (await RemindersTable.ReadRow(second.GrainId, second.ReminderName))?.ReminderName);
    }

    [SkippableFact]
    public async Task StaleETagDoesNotOverwriteReminder()
    {
        var reminder = new ReminderEntry
        {
            GrainId = GrainId.Parse($"user/{Guid.NewGuid():N}"),
            ReminderName = "stale-etag",
            StartAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Period = TimeSpan.FromMinutes(1),
        };

        reminder.ETag = await RemindersTable.UpsertRow(reminder);
        var staleETag = reminder.ETag;
        reminder.StartAt = reminder.StartAt.AddMinutes(1);
        reminder.ETag = await RemindersTable.UpsertRow(reminder);
        var currentETag = reminder.ETag;

        reminder.StartAt = reminder.StartAt.AddMinutes(1);
        reminder.ETag = staleETag;
        Assert.Null(await RemindersTable.UpsertRow(reminder));

        var stored = await RemindersTable.ReadRow(reminder.GrainId, reminder.ReminderName);
        Assert.NotNull(stored);
        Assert.Equal(currentETag, stored.ETag);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc), stored.StartAt);
    }

    [SkippableFact]
    public async Task UnspecifiedStartTimePreservesTicks()
    {
        var startAt = new DateTime(2026, 1, 1, 12, 34, 56, DateTimeKind.Unspecified);
        var reminder = new ReminderEntry
        {
            GrainId = GrainId.Parse($"user/{Guid.NewGuid():N}"),
            ReminderName = "unspecified-start",
            StartAt = startAt,
            Period = TimeSpan.FromMinutes(1),
        };

        await RemindersTable.UpsertRow(reminder);

        var stored = await RemindersTable.ReadRow(reminder.GrainId, reminder.ReminderName);
        Assert.NotNull(stored);
        Assert.Equal(startAt.Ticks, stored.StartAt.Ticks);
        Assert.Equal(DateTimeKind.Utc, stored.StartAt.Kind);
    }
}
