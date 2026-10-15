using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

/// <summary>
/// The inbox's durable wake-up reactivates a grain while a registered <see cref="IInboxWorkSource"/> holds work.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class InboxWorkSourceTests() : MessagingBehaviorTestBase(receiverOnly: false)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WriteWhichMakesWorkPending_SchedulesTheWakeupBeforeTheWriteIsStored()
    {
        var grain = NewWorkGrain();
        await grain.GetPendingAsync();
        using var write = Fixture.Storage.BlockWrite(Journal(grain));

        var adding = grain.AddWorkAsync(TimeSpan.Zero);
        await write.WaitUntilEnteredAsync();

        Assert.Equal(1, Fixture.JobManagerProbe.GetSuccessCount(MessagingPump.JobName, grain.GetGrainId()));
        write.Release();
        await adding;
        await grain.CompleteWorkAsync();
    }

    [Fact]
    public async Task CrashAfterTheWriteWhichMakesWorkPending_ReactivatesTheGrain()
    {
        var grain = NewWorkGrain();
        await grain.GetPendingAsync();
        var crashed = Fixture.GetGrainContext(grain);

        // The write becomes durable but its acknowledgement fails, so the activation fences and stops.
        Fixture.Storage.FailAfterWrite(Journal(grain));
        await Assert.ThrowsAsync<IOException>(() => grain.AddWorkAsync(TimeSpan.Zero));
        await crashed.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Token);

        await WaitForActivationsAsync(grain, 2);
        Assert.Equal(1, await grain.GetPendingAsync());
        await grain.CompleteWorkAsync();
    }

    [Fact]
    public async Task DeactivationWithPendingWork_ReactivatesTheGrain()
    {
        var grain = NewWorkGrain();
        await grain.AddWorkAsync(TimeSpan.Zero);
        var previous = Fixture.GetGrainContext(grain);

        await grain.DeactivateAsync();
        await previous.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Token);

        await WaitForActivationsAsync(grain, 2);
        Assert.Equal(1, await grain.GetPendingAsync());
        await grain.CompleteWorkAsync();
    }

    [Fact]
    public async Task Wakeup_ReschedulesAfterTheSourceDelay_AndCompletesOnceTheWorkClears()
    {
        var delay = TimeSpan.FromHours(1);
        var grain = NewWorkGrain();
        await grain.AddWorkAsync(delay);

        // The pump reschedules on the Durable Jobs clock.
        var before = Fixture.JobNow;
        var pending = await Fixture.RunPumpAsync(grain);
        var after = Fixture.JobNow;
        Assert.Equal(DurableJobRunStatus.RescheduleRequested, pending.Status);
        Assert.InRange(pending.RescheduleTime!.Value, before + delay, after + delay);

        await grain.CompleteWorkAsync();
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(grain)).Status);
    }

    [Fact]
    public async Task WritesWithoutPendingWork_DoNotScheduleAWakeup()
    {
        var grain = NewWorkGrain();

        await grain.AddWorkAsync(TimeSpan.Zero);
        await grain.CompleteWorkAsync();
        var scheduled = Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, grain.GetGrainId());
        Assert.Equal(DurableJobRunStatus.Completed, (await Fixture.RunPumpAsync(grain)).Status);

        await grain.TouchAsync();
        Assert.Equal(scheduled, Fixture.JobManagerProbe.GetAttemptCount(MessagingPump.JobName, grain.GetGrainId()));
    }

    private IWorkSourceTestGrain NewWorkGrain() => Fixture.Client.GetGrain<IWorkSourceTestGrain>(Guid.NewGuid());

    private static JournalId Journal(IAddressable grain) => JournalId.FromGrainId(grain.GetGrainId());

    private static Task WaitForActivationsAsync(IAddressable grain, int expected) =>
        WorkSourceTestGrain.WaitForActivationsAsync(grain.GetGrainId(), expected).WaitAsync(TimeSpan.FromSeconds(30), Token);
}

public interface IWorkSourceTestGrain : IGrainWithGuidKey
{
    Task AddWorkAsync(TimeSpan wakeupDelay);
    Task CompleteWorkAsync();
    Task TouchAsync();
    Task<int> GetPendingAsync();
    Task DeactivateAsync();
}

/// <summary>Holds pending work in journaled state and reports it as an inbox work source.</summary>
public sealed class WorkSourceTestGrain : Grain, IWorkSourceTestGrain, IMessagingGrain, IInboxWorkSource
{
    private static readonly ConcurrentDictionary<GrainId, ActivationLog> Activations = new();
    private readonly IJournaledStateManager _manager;
    private readonly IDurableValue<int> _pending;
    private readonly IDurableValue<long> _wakeupDelayTicks;
    private readonly IDurableValue<int> _touches;

    public WorkSourceTestGrain(
        IJournaledStateManager manager,
        IInbox inbox,
        [FromKeyedServices("work-pending")] IDurableValue<int> pending,
        [FromKeyedServices("work-wakeup-delay")] IDurableValue<long> wakeupDelayTicks,
        [FromKeyedServices("work-touches")] IDurableValue<int> touches)
    {
        _manager = manager;
        _pending = pending;
        _wakeupDelayTicks = wakeupDelayTicks;
        _touches = touches;
        inbox.Register(this);
    }

    /// <summary>Completes once the grain has activated at least <paramref name="expected"/> times.</summary>
    internal static async Task WaitForActivationsAsync(GrainId grainId, int expected)
    {
        var log = Activations.GetOrAdd(grainId, static _ => new());
        while (true)
        {
            Task activated;
            lock (log)
            {
                if (log.Count >= expected) return;
                activated = log.Activated.Task;
            }

            await activated;
        }
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var log = Activations.GetOrAdd(this.GetGrainId(), static _ => new());
        TaskCompletionSource activated;
        lock (log)
        {
            log.Count++;
            activated = log.Activated;
            log.Activated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        activated.SetResult();
        return Task.CompletedTask;
    }

    public async Task AddWorkAsync(TimeSpan wakeupDelay)
    {
        _pending.Value++;
        _wakeupDelayTicks.Value = wakeupDelay.Ticks;
        await _manager.WriteStateAsync(CancellationToken.None);
    }

    public async Task CompleteWorkAsync()
    {
        _pending.Value = 0;
        await _manager.WriteStateAsync(CancellationToken.None);
    }

    public async Task TouchAsync()
    {
        _touches.Value++;
        await _manager.WriteStateAsync(CancellationToken.None);
    }

    public Task<int> GetPendingAsync() => Task.FromResult(_pending.Value);

    public Task DeactivateAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    TimeSpan? IInboxWorkSource.GetWakeupDelay() =>
        _pending.Value > 0 ? TimeSpan.FromTicks(_wakeupDelayTicks.Value) : null;

    private sealed class ActivationLog
    {
        public int Count;
        public TaskCompletionSource Activated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
