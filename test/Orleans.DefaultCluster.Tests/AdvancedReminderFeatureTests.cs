using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using Orleans.Diagnostics;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;

namespace DefaultCluster.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
public sealed class AdvancedReminderFeatureTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task OneShot_CompletesAndRemovesDefinition_WhenRetriesAreDisabled(int failures)
    {
        await using var cluster = await CreateClusterAsync();
        var grain = cluster.Client.GetGrain<IAdvancedReminderFeatureGrain>(Guid.NewGuid());
        var name = Guid.NewGuid().ToString("N");
        using var completion = new ReminderCompletions(name);

        await grain.RegisterOneShot(name, DateTimeOffset.UtcNow, MissedReminderAction.FireImmediately, failures);
        Assert.Equal(ActivityStatusCode.Ok, await completion.NextAsync());

        Assert.Equal(1, await grain.GetTickCount());
        Assert.Null(await GetEntryAsync(cluster, grain, name));
    }

    [Theory]
    [InlineData(MissedReminderAction.FireImmediately, 1)]
    [InlineData(MissedReminderAction.Skip, 0)]
    [InlineData(MissedReminderAction.Notify, 0)]
    public async Task OverdueOneShot_AppliesMissedPolicy(MissedReminderAction action, int expectedTicks)
    {
        await using var cluster = await CreateClusterAsync();
        var grain = cluster.Client.GetGrain<IAdvancedReminderFeatureGrain>(Guid.NewGuid());
        var name = Guid.NewGuid().ToString("N");
        using var completion = new ReminderCompletions(name);

        await grain.RegisterOneShot(name, DateTimeOffset.UtcNow.AddDays(-2), action, failures: 0);
        Assert.Equal(ActivityStatusCode.Ok, await completion.NextAsync());

        Assert.Equal(expectedTicks, await grain.GetTickCount());
        Assert.Null(await GetEntryAsync(cluster, grain, name));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public async Task OneShot_RetriesSameOccurrence_AndCompletesAtConfiguredLimit(int failures)
    {
        await using var cluster = await CreateClusterAsync(maximumAttempts: 3);
        var grain = cluster.Client.GetGrain<IAdvancedReminderFeatureGrain>(Guid.NewGuid());
        var name = Guid.NewGuid().ToString("N");
        using var completion = new ReminderCompletions(name);

        await grain.RegisterOneShot(name, DateTimeOffset.UtcNow, MissedReminderAction.FireImmediately, failures);
        Assert.Equal(ActivityStatusCode.Error, await completion.NextAsync());
        Assert.Equal(ActivityStatusCode.Error, await completion.NextAsync());
        Assert.Equal(ActivityStatusCode.Ok, await completion.NextAsync());

        Assert.Equal(3, await grain.GetTickCount());
        Assert.Null(await GetEntryAsync(cluster, grain, name));
        Assert.Single(completion.JobIds.Distinct(StringComparer.Ordinal));
        Assert.Equal(new[] { 1, 2, 3 }, completion.DequeueCounts);
    }

    [Fact]
    public async Task Attribute_ReactivationPreservesRegistrationAndScheduledJob()
    {
        await using var cluster = await CreateClusterAsync();
        var grain = cluster.Client.GetGrain<IAdvancedReminderAttributeFeatureGrain>(Guid.NewGuid());
        var firstActivation = await grain.GetActivationId();
        var table = cluster.GetSiloServiceProvider().GetRequiredService<Orleans.AdvancedReminders.IReminderTable>();
        var before = Assert.IsType<ReminderEntry>(await table.ReadRow(grain.GetGrainId(), AdvancedReminderAttributeFeatureGrain.ReminderName));

        await grain.Deactivate();
        Assert.NotEqual(firstActivation, await grain.GetActivationId());
        var after = Assert.IsType<ReminderEntry>(await table.ReadRow(grain.GetGrainId(), AdvancedReminderAttributeFeatureGrain.ReminderName));

        Assert.Equal(before.ScheduleId, after.ScheduleId);
        Assert.Equal(before.NextDueUtc, after.NextDueUtc);
        Assert.Equal(before.JobId, after.JobId);
        Assert.Equal(before.ETag, after.ETag);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Interval_ContinuesAfterCallback_AndOriginalHandleCanUnregisterNextOccurrence(int failures)
    {
        await using var cluster = await CreateClusterAsync();
        var grain = cluster.Client.GetGrain<IAdvancedReminderFeatureGrain>(Guid.NewGuid());
        var name = Guid.NewGuid().ToString("N");
        using var completion = new ReminderCompletions(name);

        await grain.RegisterIntervalUntilSecondTick(name, failures);
        Assert.Equal(ActivityStatusCode.Ok, await completion.NextAsync());
        Assert.Equal(ActivityStatusCode.Ok, await completion.NextAsync());

        Assert.Equal(2, await grain.GetTickCount());
        Assert.Equal(TimeSpan.FromSeconds(1), await grain.GetLastPeriod());
        Assert.Null(await GetEntryAsync(cluster, grain, name));
        Assert.Equal(2, completion.JobIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Callback_UpdatesOwnSchedule_WithoutDeadlockOrOverwritingReplacement()
    {
        await using var cluster = await CreateClusterAsync();
        var grain = cluster.Client.GetGrain<IAdvancedReminderFeatureGrain>(Guid.NewGuid());
        var name = Guid.NewGuid().ToString("N");
        var replacementDue = DateTimeOffset.UtcNow.AddDays(30);
        using var completion = new ReminderCompletions(name);

        await grain.RegisterAndReplaceInCallback(name, replacementDue);
        Assert.Equal(ActivityStatusCode.Ok, await completion.NextAsync());

        var entry = Assert.IsType<ReminderEntry>(await GetEntryAsync(cluster, grain, name));
        Assert.Equal(replacementDue.UtcDateTime, entry.NextDueUtc);
        Assert.NotEmpty(entry.JobId);
        Assert.NotEmpty(entry.JobShardId);
        Assert.Equal(1, await grain.GetTickCount());

        await cluster.Client.GetReminderManagementGrain().DeleteAsync(grain.GetGrainId(), name);
        Assert.Null(await GetEntryAsync(cluster, grain, name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Asia/Kathmandu")]
    public async Task Cron_DeliversThroughDispatcher_AndPersistsNextOccurrence(string? timeZoneId)
    {
        await using var cluster = await CreateClusterAsync();
        var grain = cluster.Client.GetGrain<IAdvancedReminderFeatureGrain>(Guid.NewGuid());
        var name = Guid.NewGuid().ToString("N");
        using var completion = new ReminderCompletions(name);

        await grain.RegisterCron(name, "*/5 * * * * *", timeZoneId);
        Assert.Equal(ActivityStatusCode.Ok, await completion.NextAsync());

        var entry = Assert.IsType<ReminderEntry>(await GetEntryAsync(cluster, grain, name));
        Assert.Equal(timeZoneId ?? string.Empty, entry.CronTimeZoneId);
        Assert.NotNull(entry.LastFireUtc);
        Assert.True(entry.NextDueUtc > entry.LastFireUtc);
        Assert.Equal(0, entry.NextDueUtc!.Value.Second % 5);
        Assert.NotEmpty(entry.JobId);
        Assert.NotEqual(completion.JobIds[0], entry.JobId);
        Assert.Equal(TimeSpan.Zero, await grain.GetLastPeriod());

        await cluster.Client.GetReminderManagementGrain().DeleteAsync(grain.GetGrainId(), name);
        Assert.Null(await GetEntryAsync(cluster, grain, name));
    }

    [Fact]
    public async Task Management_ChangesActionAndRepairsFarFutureReminder_WithoutDeliveringIt()
    {
        await using var cluster = await CreateClusterAsync();
        var grain = cluster.Client.GetGrain<IAdvancedReminderFeatureGrain>(Guid.NewGuid());
        var name = Guid.NewGuid().ToString("N");
        var due = DateTimeOffset.UtcNow.AddDays(30);
        await grain.RegisterOneShot(name, due, MissedReminderAction.FireImmediately, failures: 0);
        var original = Assert.IsType<ReminderEntry>(await GetEntryAsync(cluster, grain, name));
        var management = cluster.Client.GetReminderManagementGrain();

        await management.SetActionAsync(grain.GetGrainId(), name, MissedReminderAction.Skip);
        await management.RepairAsync(grain.GetGrainId(), name);
        var page = await management.ListFilteredAsync(new ReminderQueryFilter { Action = MissedReminderAction.Skip }, pageSize: 1);
        var repaired = Assert.Single(page.Reminders);
        Assert.Null(page.ContinuationToken);
        Assert.Equal(due.UtcDateTime, repaired.NextDueUtc);
        Assert.NotEqual(original.ScheduleId, repaired.ScheduleId);
        Assert.NotEqual(original.JobId, repaired.JobId);
        Assert.Equal(0, await grain.GetTickCount());
        await management.DeleteAsync(grain.GetGrainId(), name);
        Assert.Null(await GetEntryAsync(cluster, grain, name));
    }

    private static async Task<InProcessTestCluster> CreateClusterAsync(int? maximumAttempts = null)
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureSilo((_, silo) =>
        {
            silo.UseInMemoryAdvancedReminderService();
            silo.Configure<Orleans.AdvancedReminders.ReminderOptions>(options =>
            {
                options.MinimumReminderPeriod = TimeSpan.FromSeconds(1);
                options.MissedReminderGracePeriod = TimeSpan.FromDays(1);
                options.MaximumDeliveryAttempts = maximumAttempts;
            });
            silo.Configure<DurableJobsOptions>(options =>
                options.ShouldRetry = (context, _) => context.DequeueCount < 3 ? DateTimeOffset.UtcNow : null);
        });
        var cluster = builder.Build();
        try
        {
            await cluster.DeployAsync(TestContext.Current.CancellationToken);
            return cluster;
        }
        catch
        {
            await cluster.DisposeAsync();
            throw;
        }
    }

    private static Task<ReminderEntry?> GetEntryAsync(InProcessTestCluster cluster, IAdvancedReminderFeatureGrain grain, string name)
        => cluster.GetSiloServiceProvider().GetRequiredService<Orleans.AdvancedReminders.IReminderTable>().ReadRow(grain.GetGrainId(), name);

    // The existing handler activity stops after the dispatcher has persisted its result.
    // Subscribe before registration so immediate jobs cannot race test readiness.
    private sealed class ReminderCompletions : IDisposable
    {
        private readonly Channel<(ActivityStatusCode Status, string JobId, int DequeueCount)> _completions
            = Channel.CreateUnbounded<(ActivityStatusCode, string, int)>();
        private readonly ActivityListener _listener;
        private readonly string _name;
        public List<string> JobIds { get; } = [];
        public List<int> DequeueCounts { get; } = [];

        public ReminderCompletions(string name)
        {
            _name = name;
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ActivitySources.DurableJobsActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (activity.OperationName == "execute durable job handler"
                        && Equals(activity.GetTagItem(ActivityTagKeys.DurableJobName), "advanced-reminder:" + name))
                    {
                        _completions.Writer.TryWrite((
                            activity.Status,
                            (string)activity.GetTagItem(ActivityTagKeys.DurableJobId)!,
                            (int)activity.GetTagItem(ActivityTagKeys.DurableJobDequeueCount)!));
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public async Task<ActivityStatusCode> NextAsync()
        {
            try
            {
                var completion = await _completions.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                JobIds.Add(completion.JobId);
                DequeueCounts.Add(completion.DequeueCount);
                return completion.Status;
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException($"Reminder '{_name}': dispatcher completion {DequeueCounts.Count + 1} was not observed.", exception);
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}

public interface IAdvancedReminderFeatureGrain : IGrainWithGuidKey
{
    Task RegisterOneShot(string name, DateTimeOffset due, MissedReminderAction action, int failures);
    Task RegisterCron(string name, string expression, string? timeZoneId);
    Task RegisterAndReplaceInCallback(string name, DateTimeOffset replacementDue);
    Task RegisterIntervalUntilSecondTick(string name, int failures);
    Task<int> GetTickCount();
    Task<TimeSpan> GetLastPeriod();
}

public sealed class AdvancedReminderFeatureGrain : Grain, IAdvancedReminderFeatureGrain, Orleans.AdvancedReminders.IRemindable
{
    private int _tickCount;
    private int _failures;
    private TimeSpan _lastPeriod;
    private DateTimeOffset? _replacementDue;
    private Orleans.AdvancedReminders.IGrainReminder? _originalHandle;

    public async Task RegisterOneShot(string name, DateTimeOffset due, MissedReminderAction action, int failures)
    {
        _failures = failures;
        await this.RegisterOrUpdateAdvancedReminder(name, due, action);
    }

    public async Task RegisterCron(string name, string expression, string? timeZoneId)
        => await this.RegisterOrUpdateAdvancedReminder(name, ReminderSchedule.Cron(expression, timeZoneId));

    public async Task RegisterIntervalUntilSecondTick(string name, int failures)
    {
        _failures = failures;
        _originalHandle = await this.RegisterOrUpdateAdvancedReminder(
            name, ReminderSchedule.Interval(TimeSpan.Zero, TimeSpan.FromSeconds(1)), MissedReminderAction.FireImmediately);
    }

    public async Task RegisterAndReplaceInCallback(string name, DateTimeOffset replacementDue)
    {
        _replacementDue = replacementDue;
        await RegisterOneShot(name, DateTimeOffset.UtcNow, MissedReminderAction.FireImmediately, failures: 0);
    }

    public Task<int> GetTickCount() => Task.FromResult(_tickCount);
    public Task<TimeSpan> GetLastPeriod() => Task.FromResult(_lastPeriod);

    public async Task ReceiveReminder(string reminderName, Orleans.AdvancedReminders.Runtime.TickStatus status)
    {
        _tickCount++;
        _lastPeriod = status.Period;
        if (_tickCount == 2 && _originalHandle is not null)
        {
            await this.UnregisterAdvancedReminder(_originalHandle);
        }

        if (_replacementDue is { } due)
        {
            _replacementDue = null;
            await this.RegisterOrUpdateAdvancedReminder(reminderName, due);
        }

        if (_tickCount <= _failures)
        {
            throw new InvalidOperationException("Intentional callback failure for retry verification.");
        }
    }
}

public interface IAdvancedReminderAttributeFeatureGrain : IGrainWithGuidKey
{
    Task<Guid> GetActivationId();
    Task Deactivate();
}

[Orleans.AdvancedReminders.RegisterReminder(ReminderName, dueSeconds: 3600, periodSeconds: 3600)]
public sealed class AdvancedReminderAttributeFeatureGrain : Grain, IAdvancedReminderAttributeFeatureGrain, Orleans.AdvancedReminders.IRemindable
{
    public const string ReminderName = "attribute-feature";
    private readonly Guid _activationId = Guid.NewGuid();

    public Task<Guid> GetActivationId() => Task.FromResult(_activationId);

    public Task Deactivate()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public Task ReceiveReminder(string reminderName, Orleans.AdvancedReminders.Runtime.TickStatus status) => Task.CompletedTask;
}
