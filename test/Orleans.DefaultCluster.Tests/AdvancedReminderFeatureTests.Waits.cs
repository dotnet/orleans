using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using Orleans.DurableJobs;
using Orleans.TestingHost;
using Xunit;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using ReminderTableData = Orleans.AdvancedReminders.ReminderTableData;

namespace DefaultCluster.Tests;

public sealed partial class AdvancedReminderFeatureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledDependency_ReleasesDispatcher_AndIgnoresTheLateResult(bool scheduling)
    {
        var clock = new ReminderWaitClock(DateTimeOffset.UtcNow);
        var gate = new ReminderDependencyGate(scheduling);
        await using var cluster = await CreateClusterWithGateAsync(gate, clock);
        var grain = cluster.Client.GetGrain<IAdvancedReminderFeatureGrain>(Guid.NewGuid());
        gate.Target = grain.GetGrainId();
        var name = Guid.NewGuid().ToString("N");
        var replacementDue = clock.GetUtcNow().AddDays(31);
        var registration = grain.RegisterOneShot(name, clock.GetUtcNow().AddDays(30), MissedReminderAction.FireImmediately, 0);
        try
        {
            await AwaitPhase(gate.Started.Task, "the provider dependency is stalled inside its owner turn");
            await AwaitPhase(clock.DeadlineArmed.Task, "the reminder dependency timeout is armed before advancing time");
            var following = grain.RegisterOneShot(name, replacementDue, MissedReminderAction.FireImmediately, 0);
            Assert.False(following.IsCompleted);
            clock.Advance(TimeSpan.FromMinutes(1));

            await AwaitPhase(Assert.ThrowsAsync<TimeoutException>(() => registration), "the owner exits the stalled dependency wait");
            await AwaitPhase(following, "the next recipient registration passes through the dispatcher");
            var replacement = Assert.IsType<ReminderEntry>(await GetEntryAsync(cluster, grain, name));
            Assert.Equal(replacementDue.UtcDateTime, replacement.NextDueUtc);
            Assert.NotEmpty(replacement.JobId);

            gate.Release.TrySetResult();
            await AwaitPhase(gate.Finished.Task, "the abandoned provider request returns after replacement");
            if (gate.StalledRequest is { } request)
            {
                Assert.True(gate.RequestToken.IsCancellationRequested);
                var service = cluster.GetSiloServiceProvider().GetRequiredService<Orleans.AdvancedReminders.IReminderService>();
                await AwaitPhase(service.ProcessDueReminderAsync(
                    grain.GetGrainId(), name, request.Metadata!["schedule-id"], TestContext.Current.CancellationToken),
                    "a job created by the abandoned request is fenced");
            }

            var retained = Assert.IsType<ReminderEntry>(await GetEntryAsync(cluster, grain, name));
            Assert.Equal(replacement.ETag, retained.ETag);
            Assert.Equal(replacement.ScheduleId, retained.ScheduleId);
            Assert.Equal(replacement.JobId, retained.JobId);
            Assert.Equal(0, await grain.GetTickCount());
        }
        finally
        {
            gate.Release.TrySetResult();
            if (gate.Started.Task.IsCompleted)
            {
                await AwaitPhase(gate.Finished.Task, "the stalled provider request is drained");
            }
        }
    }

    [Fact]
    public async Task SchedulingReplyLostAfterPersistence_StillDelivers_WithoutResurrectingCompletedOneShot()
    {
        var clock = new ReminderWaitClock(DateTimeOffset.UtcNow);
        var gate = new ReminderDependencyGate(scheduling: true, afterPersistence: true);
        await using var cluster = await CreateClusterWithGateAsync(gate, clock);
        var grain = cluster.Client.GetGrain<IAdvancedReminderFeatureGrain>(Guid.NewGuid());
        gate.Target = grain.GetGrainId();
        var name = Guid.NewGuid().ToString("N");
        using var completion = new ReminderCompletions(name);
        var registration = grain.RegisterOneShot(name, clock.GetUtcNow(), MissedReminderAction.FireImmediately, 0);
        try
        {
            await AwaitPhase(gate.Started.Task, "the due job is persisted but its scheduling reply is held");
            await AwaitPhase(clock.DeadlineArmed.Task, "the scheduling timeout is armed before advancing time");
            clock.Advance(TimeSpan.FromMinutes(1));
            await AwaitPhase(Assert.ThrowsAsync<TimeoutException>(() => registration), "the dispatcher leaves scheduling after the lost reply");
            Assert.Equal(ActivityStatusCode.Ok, await completion.NextAsync());
            Assert.Equal(1, await grain.GetTickCount());
            Assert.Null(await GetEntryAsync(cluster, grain, name));

            gate.Release.TrySetResult();
            await AwaitPhase(gate.Finished.Task, "the late scheduling acknowledgement is drained");
            Assert.Null(await GetEntryAsync(cluster, grain, name));
            Assert.Equal(1, await grain.GetTickCount());
        }
        finally
        {
            gate.Release.TrySetResult();
            if (gate.Started.Task.IsCompleted)
            {
                await AwaitPhase(gate.Finished.Task, "the held scheduling reply is released");
            }
        }
    }

    private static Task<InProcessTestCluster> CreateClusterWithGateAsync(ReminderDependencyGate gate, TimeProvider clock)
        => CreateClusterAsync(reminderClock: clock, configureServices: services =>
        {
            var table = services.Last(static descriptor => descriptor.ServiceType == typeof(Orleans.AdvancedReminders.IReminderTable));
            var jobs = services.Last(static descriptor => descriptor.ServiceType == typeof(ILocalDurableJobManager));
            services.Remove(table);
            services.Remove(jobs);
            services.AddSingleton<Orleans.AdvancedReminders.IReminderTable>(provider =>
            {
                gate.Table = (Orleans.AdvancedReminders.IReminderTable)table.ImplementationFactory!(provider);
                return gate;
            });
            services.AddSingleton<ILocalDurableJobManager>(provider =>
            {
                gate.Jobs = (ILocalDurableJobManager)jobs.ImplementationFactory!(provider);
                return gate;
            });
        });

    private sealed class ReminderWaitClock(DateTimeOffset now) : FakeTimeProvider(now)
    {
        public TaskCompletionSource DeadlineArmed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromMinutes(1) && period == Timeout.InfiniteTimeSpan)
            {
                DeadlineArmed.TrySetResult();
            }
            return timer;
        }
    }

    private sealed class ReminderDependencyGate(bool scheduling, bool afterPersistence = false) : Orleans.AdvancedReminders.IReminderTable, ILocalDurableJobManager
    {
        private int _stalled;
        public GrainId Target { get; set; }
        public Orleans.AdvancedReminders.IReminderTable Table { get; set; } = null!;
        public ILocalDurableJobManager Jobs { get; set; } = null!;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ScheduleJobRequest? StalledRequest { get; private set; }
        public CancellationToken RequestToken { get; private set; }

        public async Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
        {
            if (!scheduling && grainId == Target && Interlocked.Exchange(ref _stalled, 1) == 0)
            {
                Started.TrySetResult();
                try
                {
                    await Release.Task;
                    return await Table.ReadRow(grainId, reminderName);
                }
                finally { Finished.TrySetResult(); }
            }
            return await Table.ReadRow(grainId, reminderName);
        }

        public async Task<DurableJob> ScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
        {
            if (scheduling && request.Metadata?.GetValueOrDefault("grain-id") == Target.ToString()
                && Interlocked.Exchange(ref _stalled, 1) == 0)
            {
                StalledRequest = request;
                RequestToken = cancellationToken;
                try
                {
                    var persisted = afterPersistence ? await Jobs.ScheduleJobAsync(request, CancellationToken.None) : null;
                    Started.TrySetResult();
                    await Release.Task;
                    return persisted ?? await Jobs.ScheduleJobAsync(request, CancellationToken.None);
                }
                finally { Finished.TrySetResult(); }
            }
            return await Jobs.ScheduleJobAsync(request, cancellationToken);
        }

        public Task<bool> CancelAsync(DurableJob job, CancellationToken cancellationToken) => Jobs.CancelAsync(job, cancellationToken);
        public Task StartAsync(CancellationToken cancellationToken = default) => Table.StartAsync(cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken = default) => Table.StopAsync(cancellationToken);
        public Task<ReminderTableData> ReadRows(GrainId grainId) => Table.ReadRows(grainId);
        public Task<ReminderTableData> ReadRows(uint begin, uint end) => Table.ReadRows(begin, end);
        public Task<ReminderTableData> ReadRows(uint begin, uint end, int maxRows, string? continuationToken) => Table.ReadRows(begin, end, maxRows, continuationToken);
        public Task<string> UpsertRow(ReminderEntry entry) => Table.UpsertRow(entry);
        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag) => Table.RemoveRow(grainId, reminderName, eTag);
        public Task TestOnlyClearTable() => Table.TestOnlyClearTable();
    }
}
