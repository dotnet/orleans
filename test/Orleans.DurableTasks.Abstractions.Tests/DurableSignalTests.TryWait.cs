using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

public partial class DurableSignalTests
{
    [Fact]
    public async Task TryWaitReturnsTheSignalWhenItWinsTheRaceWithTheDeadline()
    {
        var host = new SignalHost();
        var delay = new TaskCompletionSource<DurableTaskResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Delay = (_, _) => new(delay.Task);
        var running = DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ => await Approved.TryWaitAsync(Deadline)),
            new SignalContext(host, TaskId.CreateRoot("workflow"))).AsTask();
        await host.WaitStarted("approved");

        host.Send("approved", DurableTaskResponse.FromResult(new Approval("ada")));

        Assert.Equal((true, new Approval("ada")), (await running).GetResult<(bool, Approval)>());
        Assert.Equal(Deadline, host.LastDueTime);

        // The race schedules the wait and the deadline in indexed slots, and reads the winner's recorded result.
        Assert.Equal(
            [(TaskId.Parse("workflow/$when-any-1/0"), "approved")],
            host.Waits.Select(static wait => (wait.ExecutionId, wait.Name)));
        Assert.Equal(
            [TaskId.Parse("workflow/$when-any-1/0"), TaskId.Parse("workflow/$when-any-1/1")],
            host.Children.OrderBy(static id => id.ToString(), StringComparer.Ordinal));
        delay.SetResult(DurableTaskResponse.Completed);
    }

    [Fact]
    public async Task TryWaitReturnsNoValueWhenTheDeadlineWins()
    {
        var host = new SignalHost { Delay = static (_, _) => new(DurableTaskResponse.Completed) };

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ => await Approved.TryWaitAsync(Deadline)),
            new SignalContext(host, TaskId.CreateRoot("workflow")));

        Assert.Equal((false, (Approval?)null), response.GetResult<(bool, Approval)>());
        Assert.Equal(Deadline, host.LastDueTime);
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
    public async Task TryWaitPropagatesACanceledDeadline()
    {
        var host = new SignalHost { Delay = static (_, _) => new(DurableTaskResponse.Canceled) };

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async _ => await Approved.TryWaitAsync(Deadline)),
            new SignalContext(host, TaskId.CreateRoot("workflow")));

        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
    }

    [Fact]
    public async Task TryWaitRequiresADurableExecution()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Approved.TryWaitAsync(Deadline));
    }
}
