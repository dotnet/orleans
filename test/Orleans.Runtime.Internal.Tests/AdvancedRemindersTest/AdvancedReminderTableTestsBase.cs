#nullable enable
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.TestingHost.Utils;
using TestExtensions;
using UnitTests.MembershipTests;
using Xunit;
using AdvancedReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using AdvancedReminderTableData = Orleans.AdvancedReminders.ReminderTableData;
using AdvancedReminderTable = Orleans.AdvancedReminders.IReminderTable;
using ReminderPriority = Orleans.AdvancedReminders.Runtime.ReminderPriority;
using MissedReminderAction = Orleans.AdvancedReminders.Runtime.MissedReminderAction;

namespace UnitTests.AdvancedRemindersTest;

[Collection(TestEnvironmentFixture.DefaultCollection)]
public abstract class AdvancedReminderTableTestsBase : IAsyncLifetime, IClassFixture<ConnectionStringFixture>
{
    protected readonly TestEnvironmentFixture ClusterFixture;
    protected readonly ILoggerFactory loggerFactory;
    protected readonly IOptions<ClusterOptions> clusterOptions;
    protected readonly ConnectionStringFixture connectionStringFixture;

    private readonly AdvancedReminderTable remindersTable;

    protected const string testDatabaseName = "OrleansAdvancedReminderTest";

    protected AdvancedReminderTableTestsBase(ConnectionStringFixture fixture, TestEnvironmentFixture clusterFixture, LoggerFilterOptions filters)
    {
        connectionStringFixture = fixture;
        fixture.InitializeConnectionStringAccessor(GetConnectionString);
        loggerFactory = TestingUtils.CreateDefaultLoggerFactory($"{GetType()}.log", filters);
        ClusterFixture = clusterFixture;

        var serviceId = Guid.NewGuid() + "/advanced-reminders";
        var clusterId = "test-" + serviceId + "/cluster";
        clusterOptions = Options.Create(new ClusterOptions { ClusterId = clusterId, ServiceId = serviceId });

        remindersTable = CreateRemindersTable();
    }

    public virtual async Task InitializeAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await remindersTable.StartAsync(cancellation.Token);
    }

    public virtual async Task DisposeAsync()
    {
        if (SiloInstanceTableTestConstants.DeleteEntriesAfterTest)
        {
            await remindersTable.TestOnlyClearTable();
        }
    }

    protected abstract AdvancedReminderTable CreateRemindersTable();
    protected abstract Task<string> GetConnectionString();

    protected virtual string? GetAdoInvariant() => null;

    [SkippableFact]
    public async Task RemindersTable_DurableSimpleRoundTrip() => await ReminderSimple();

    [SkippableFact]
    public async Task RemindersTable_DurableParallelUpsert() => await RemindersParallelUpsert();

    [SkippableFact]
    public async Task RemindersTable_DurableRangeQueries() => await RemindersRange(iterations: 128);

    protected async Task RemindersParallelUpsert()
    {
        var upserts = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
        {
            var reminder = CreateReminder(MakeTestGrainReference(), i.ToString());
            return Task.WhenAll(Enumerable.Range(1, 5).Select(_ =>
                RetryHelper.RetryOnExceptionAsync(5, RetryOperation.Sigmoid, () => remindersTable.UpsertRow(reminder))));
        }));

        Assert.DoesNotContain(upserts, values => values.Distinct().Count() != 5);
    }

    protected async Task ReminderSimple()
    {
        var reminder = CreateReminder(MakeTestGrainReference(), "foo/bar\\#b_a_z?");
        await remindersTable.UpsertRow(reminder);

        var readReminder = await remindersTable.ReadRow(reminder.GrainId, reminder.ReminderName);
        Assert.NotNull(readReminder);

        var previousETag = reminder.ETag = readReminder.ETag;

        Assert.Equal(readReminder.GrainId, reminder.GrainId);
        Assert.Equal(readReminder.Period, reminder.Period);
        Assert.Equal(readReminder.ReminderName, reminder.ReminderName);
        Assert.Equal(readReminder.StartAt, reminder.StartAt);
        Assert.Equal(ReminderPriority.Normal, readReminder.Priority);
        Assert.Equal(MissedReminderAction.Skip, readReminder.Action);

        reminder.ETag = await remindersTable.UpsertRow(reminder);

        Assert.False(await remindersTable.RemoveRow(reminder.GrainId, reminder.ReminderName, previousETag));
        Assert.False(await remindersTable.RemoveRow(reminder.GrainId, "missing", reminder.ETag));
        Assert.True(await remindersTable.RemoveRow(reminder.GrainId, reminder.ReminderName, reminder.ETag));
        Assert.False(await remindersTable.RemoveRow(reminder.GrainId, reminder.ReminderName, reminder.ETag));
    }

    protected async Task ReminderCronRoundTrip()
    {
        var reminder = CreateReminder(MakeTestGrainReference(), "cron_roundtrip");
        reminder.CronExpression = "0 */5 * * * *";
        reminder.Period = TimeSpan.Zero;

        await remindersTable.UpsertRow(reminder);
        var readReminder = await remindersTable.ReadRow(reminder.GrainId, reminder.ReminderName);

        Assert.NotNull(readReminder);
        Assert.Equal(reminder.CronExpression, readReminder.CronExpression);
        Assert.Equal(TimeSpan.Zero, readReminder.Period);
    }

    protected async Task ReminderAdaptiveFieldsRoundTrip()
    {
        var reminder = CreateReminder(MakeTestGrainReference(), "adaptive_roundtrip");
        reminder.CronExpression = "0 */10 * * * *";
        reminder.Period = TimeSpan.Zero;
        reminder.NextDueUtc = DateTime.UtcNow.AddMinutes(10);
        reminder.LastFireUtc = DateTime.UtcNow.AddMinutes(-2);
        reminder.Priority = ReminderPriority.High;
        reminder.Action = MissedReminderAction.FireImmediately;

        await remindersTable.UpsertRow(reminder);
        var readReminder = await remindersTable.ReadRow(reminder.GrainId, reminder.ReminderName);

        Assert.NotNull(readReminder);
        Assert.Equal(reminder.CronExpression, readReminder.CronExpression);
        AssertTimestampClose(reminder.NextDueUtc, readReminder.NextDueUtc);
        AssertTimestampClose(reminder.LastFireUtc, readReminder.LastFireUtc);
        Assert.Equal(reminder.Priority, readReminder.Priority);
        Assert.Equal(reminder.Action, readReminder.Action);
    }

    protected async Task ReminderCronTimeZoneRoundTrip()
    {
        var reminder = CreateReminder(MakeTestGrainReference(), "cron_timezone_roundtrip");
        reminder.CronExpression = "0 */5 * * * *";
        reminder.CronTimeZoneId = "Europe/Warsaw";
        reminder.Period = TimeSpan.Zero;

        await remindersTable.UpsertRow(reminder);
        var readReminder = await remindersTable.ReadRow(reminder.GrainId, reminder.ReminderName);

        Assert.NotNull(readReminder);
        Assert.Equal(reminder.CronExpression, readReminder.CronExpression);
        Assert.Equal(reminder.CronTimeZoneId, readReminder.CronTimeZoneId);

        reminder.ETag = readReminder.ETag;
        reminder.CronExpression = "0 */10 * * * *";
        reminder.CronTimeZoneId = "America/New_York";
        await remindersTable.UpsertRow(reminder);

        readReminder = await remindersTable.ReadRow(reminder.GrainId, reminder.ReminderName);
        Assert.NotNull(readReminder);
        Assert.Equal(reminder.CronExpression, readReminder.CronExpression);
        Assert.Equal(reminder.CronTimeZoneId, readReminder.CronTimeZoneId);
    }

    protected async Task RemindersRange(int iterations = 1000)
    {
        await Task.WhenAll(Enumerable.Range(1, iterations).Select(async i =>
        {
            var grainRef = MakeTestGrainReference();
            await RetryHelper.RetryOnExceptionAsync(10, RetryOperation.Sigmoid, async () =>
            {
                await remindersTable.UpsertRow(CreateReminder(grainRef, i.ToString()));
                return 0;
            });
        }));

        var rows = await remindersTable.ReadRows(0, uint.MaxValue);
        Assert.Equal(iterations, rows.Reminders.Count);

        rows = await remindersTable.ReadRows(0, 0);
        Assert.Equal(iterations, rows.Reminders.Count);

        var reminderHashes = rows.Reminders.Select(r => r.GrainId.GetUniformHashCode()).ToArray();
        await Task.WhenAll(Enumerable.Range(0, iterations).Select(_ =>
            TestRemindersHashInterval(remindersTable,
                (uint)Random.Shared.Next(int.MinValue, int.MaxValue),
                (uint)Random.Shared.Next(int.MinValue, int.MaxValue),
                reminderHashes)));
    }

    private static async Task TestRemindersHashInterval(AdvancedReminderTable reminderTable, uint beginHash, uint endHash, uint[] reminderHashes)
    {
        var rowsTask = reminderTable.ReadRows(beginHash, endHash);
        var expectedHashes = beginHash < endHash
            ? reminderHashes.Where(r => r > beginHash && r <= endHash)
            : reminderHashes.Where(r => r > beginHash || r <= endHash);

        var expectedSet = new HashSet<uint>(expectedHashes);
        var returnedSet = new HashSet<uint>((await rowsTask).Reminders.Select(r => r.GrainId.GetUniformHashCode()));

        Assert.True(returnedSet.SetEquals(expectedSet));
    }

    private static AdvancedReminderEntry CreateReminder(GrainId grainId, string reminderName)
    {
        var now = DateTime.UtcNow;
        now = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Utc);
        return new AdvancedReminderEntry
        {
            GrainId = grainId,
            ReminderName = reminderName,
            Period = TimeSpan.FromMinutes(1),
            StartAt = now,
        };
    }

    private static GrainId MakeTestGrainReference() => LegacyGrainId.GetGrainId(12345, Guid.NewGuid(), "foo/bar\\#baz?");

    private static void AssertTimestampClose(DateTime? expected, DateTime? actual)
    {
        Assert.Equal(expected.HasValue, actual.HasValue);
        if (!expected.HasValue)
        {
            return;
        }

        var difference = (expected.Value - actual!.Value).Duration();
        Assert.True(
            difference <= TimeSpan.FromSeconds(1),
            $"Expected timestamps to be within 1 second. Expected: {expected.Value:O}, Actual: {actual.Value:O}, Difference: {difference}.");
    }
}
