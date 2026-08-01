using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnitTests;
using TestExtensions;
using UnitTests.RemindersTest;
using Orleans.Reminders.GoogleFirestore;
using Orleans.Runtime;


namespace Orleans.Tests.Google;

[TestCategory("Reminders"), TestCategory("GoogleFirestore"), TestCategory("GoogleCloud"), TestCategory("Functional")]
[Collection(TestEnvironmentFixture.DefaultCollection)]
public class FirestoreRemindersTests : ReminderTableTestsBase
{
    public FirestoreRemindersTests(ConnectionStringFixture fixture)
        : base(fixture, new TestEnvironmentFixture(), new LoggerFilterOptions())
    {
    }

    protected override IReminderTable CreateRemindersTable()
    {
        var options = new FirestoreOptions
        {
            ProjectId = GoogleEmulatorHost.ProjectId,
            EmulatorHost = GoogleEmulatorHost.FirestoreEndpoint
        };

        return new GoogleFirestoreReminderTable(
            this.loggerFactory,
            this.clusterOptions,
            Options.Create(options));
    }

    protected override Task<string> GetConnectionString() => Task.FromResult(GoogleEmulatorHost.FirestoreEndpoint);

    [SkippableFact]
    public void Init()
    {
    }

    [SkippableFact]
    public async Task Range()
    {
        await RemindersRange(50);
    }

    [SkippableFact]
    public async Task ParallelUpsert()
    {
        await RemindersParallelUpsert();
    }

    [SkippableFact]
    public async Task Simple()
    {
        await ReminderSimple();
    }

    [SkippableFact]
    public async Task ReminderIdsDoNotCollide()
    {
        var startAt = DateTime.UtcNow;
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
}
