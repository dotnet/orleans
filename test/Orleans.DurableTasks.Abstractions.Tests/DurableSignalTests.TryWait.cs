using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

public partial class DurableSignalTests
{
    private const string RaceLostReason = "Another task won the race.";
    private static readonly TaskId SignalSlot = TaskId.Parse("workflow/$race-1/0");
    private static readonly TaskId DeadlineSlot = TaskId.Parse("workflow/$race-1/1");

    [Fact]
    public async Task TryWaitReturnsTheSignalAndCancelsTheDeadlineWhenTheSignalWins()
    {
        var host = new SignalHost();
        var running = DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ => await Approved.TryWaitAsync(Deadline)),
            new SignalContext(host, TaskId.CreateRoot("workflow"))).AsTask();
        await host.WaitStarted("approved");

        host.Send("approved", DurableTaskResponse.FromResult(new Approval("ada")));

        Assert.Equal((true, new Approval("ada")), (await running).GetResult<(bool, Approval)>());
        Assert.Equal(Deadline, host.LastDueTime);

        // The race schedules the wait and the deadline in indexed slots, and completes once the deadline's delay, which
        // lost, has ended canceled.
        Assert.Equal(
            [(SignalSlot, "approved")],
            host.Waits.Select(static wait => (wait.ExecutionId, wait.Name)));
        Assert.Equal([SignalSlot, DeadlineSlot], host.Children.OrderBy(static id => id.ToString(), StringComparer.Ordinal));
        var deadline = host.GetChild(DeadlineSlot);
        Assert.Equal(DurableTaskStatus.Canceled, deadline.Status);
        Assert.Equal(RaceLostReason, deadline.CancellationReason);
        Assert.Null(host.GetChild(SignalSlot).CancellationReason);
    }

    [Fact]
    public async Task TryWaitReturnsNoValueAndCancelsTheWaitWhenTheDeadlineWins()
    {
        var host = new SignalHost { Delay = static (_, _) => new(DurableTaskResponse.Completed) };

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ => await Approved.TryWaitAsync(Deadline)),
            new SignalContext(host, TaskId.CreateRoot("workflow")));

        Assert.Equal((false, (Approval?)null), response.GetResult<(bool, Approval)>());
        Assert.Equal(Deadline, host.LastDueTime);

        // The race completes once the wait, which lost, has ended canceled.
        var wait = host.GetChild(SignalSlot);
        Assert.Equal(DurableTaskStatus.Canceled, wait.Status);
        Assert.Equal(RaceLostReason, wait.CancellationReason);
        Assert.Null(host.GetChild(DeadlineSlot).CancellationReason);
    }

    [Fact]
    public async Task TryWaitWithIdNamesTheRace()
    {
        var host = new SignalHost { Delay = static (_, _) => new(DurableTaskResponse.Completed) };

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ => (await Approved.TryWaitAsync(Deadline).WithId("approval")).Received),
            new SignalContext(host, TaskId.CreateRoot("workflow")));

        Assert.False(response.GetResult<bool>());
        Assert.Equal(
            [TaskId.Parse("workflow/approval/0"), TaskId.Parse("workflow/approval/1")],
            host.Children.OrderBy(static id => id.ToString(), StringComparer.Ordinal));
    }

    [Fact]
    public async Task TryWaitPropagatesACanceledDeadlineOnceTheWaitHasEnded()
    {
        var host = new SignalHost { Delay = static (_, _) => new(DurableTaskResponse.Canceled) };

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ => await Approved.TryWaitAsync(Deadline)),
            new SignalContext(host, TaskId.CreateRoot("workflow")));

        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        Assert.Equal(DurableTaskStatus.Canceled, host.GetChild(SignalSlot).Status);
    }

    [Fact]
    public async Task TryWaitRequiresADurableExecution()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Approved.TryWaitAsync(Deadline));
    }
}
