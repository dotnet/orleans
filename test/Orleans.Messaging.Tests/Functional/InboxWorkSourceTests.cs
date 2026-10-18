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
    public async Task NegativeSourceDelay_SchedulesAPromptWakeupBeforeTheWriteIsStored()
    {
        var grain = NewWorkGrain();
        await grain.GetPendingAsync();
        using var write = Fixture.Storage.BlockWrite(Journal(grain));

        var adding = grain.AddWorkAsync(TimeSpan.MinValue);
        await write.WaitUntilEnteredAsync();

        Assert.Equal(1, Fixture.JobManagerProbe.GetSuccessCount(MessagingPump.JobName, grain.GetGrainId()));
        write.Release();
        await adding;
        Assert.Equal(1, await grain.GetPendingAsync());
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

        var before = DateTimeOffset.UtcNow;
        var pending = await Fixture.RunPumpAsync(grain);
        var after = DateTimeOffset.UtcNow;
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

    private static async Task WaitForActivationsAsync(IAddressable grain, int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (WorkSourceTestGrain.GetActivationCount(grain.GetGrainId()) < expected)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Grain '{grain.GetGrainId()}' was not reactivated.");
            }

            await Task.Delay(10, Token);
        }
    }
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
    private static readonly ConcurrentDictionary<GrainId, int> Activations = new();
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

    internal static int GetActivationCount(GrainId grainId) => Activations.TryGetValue(grainId, out var count) ? count : 0;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        Activations.AddOrUpdate(this.GetGrainId(), 1, static (_, count) => count + 1);
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
}
