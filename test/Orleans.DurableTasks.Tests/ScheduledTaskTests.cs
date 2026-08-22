#nullable enable
using System.Distributed.DurableTasks;
using NSubstitute;
using Orleans.Runtime.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Tests;

[TestCategory("BVT")]
public class ScheduledTaskTests
{
    [Fact]
    public async Task CompletedScheduledTask_ReportsAndReturnsCompletedResponse()
    {
        var taskId = TaskId.Create("completed");
        var response = DurableTaskResponse.FromResult(42);
        ScheduledTask task = new CompletedScheduledDurableTask(taskId, response);

        Assert.Equal(taskId, task.Id);
        Assert.True(await task.IsCompletedAsync());
        Assert.True(await task.IsCompletedAsync(new PollingOptions { PollTimeout = TimeSpan.FromSeconds(1) }));
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, await task.GetStatusAsync());
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, await task.GetStatusAsync(new PollingOptions()));
        Assert.Same(response, await task.GetResponseAsync());
        Assert.Same(response, await task.GetResponseAsync(new PollingOptions()));
        await task.WaitAsync();
        await task.CancelAsync();
        await task;
    }

    [Fact]
    public async Task CompletedGenericScheduledTask_AwaitReturnsTypedResult()
    {
        var taskId = TaskId.Create("completed-generic");
        var response = DurableTaskResponse.FromResult(73);
        ScheduledTask<int> task = new CompletedScheduledDurableTask<int>(taskId, response);

        var result = await task;
        var waited = await task.WaitAsync();

        Assert.Equal(73, result);
        Assert.Equal(73, waited);
        Assert.Same(response, await task.GetResponseAsync());
        Assert.Equal(taskId, task.Id);
    }

    [Fact]
    public async Task CompletedScheduledTask_AwaitPropagatesFailure()
    {
        var expected = new InvalidOperationException("Expected failure.");
        ScheduledTask task = new CompletedScheduledDurableTask(
            TaskId.Create("failed"),
            DurableTaskResponse.FromException(expected));

        var awaitException = await Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
        Assert.Same(expected, awaitException);

        var waitException = await Assert.ThrowsAsync<InvalidOperationException>(async () => await task.WaitAsync());
        Assert.Same(expected, waitException);
    }

    [Fact]
    public async Task CompletedScheduledTask_AwaitPropagatesCancellation()
    {
        var expected = new OperationCanceledException("Expected cancellation.");
        ScheduledTask task = new CompletedScheduledDurableTask(
            TaskId.Create("canceled"),
            DurableTaskResponse.FromException(expected));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.Same(expected, exception);
    }

    [Fact]
    public async Task ScheduledDurableTask_DelegatesToHandle()
    {
        var taskId = TaskId.Create("scheduled");
        var handle = Substitute.For<IScheduledTaskHandle>();
        handle.TaskId.Returns(taskId);
        handle.PollAsync(default!, default)
            .ReturnsForAnyArgs(new ValueTask<DurableTaskResponse>(DurableTaskResponse.Pending));
        handle.WaitAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<DurableTaskResponse>(DurableTaskResponse.Completed));
        ScheduledTask task = new ScheduledDurableTask(handle);
        var pollingOptions = new PollingOptions { PollTimeout = TimeSpan.FromSeconds(2) };

        Assert.Equal(taskId, task.Id);
        Assert.False(await task.IsCompletedAsync(pollingOptions));
        Assert.Equal(DurableTaskStatus.Pending, await task.GetStatusAsync(pollingOptions));
        Assert.Same(DurableTaskResponse.Pending, await task.GetResponseAsync(pollingOptions));
        Assert.Same(DurableTaskResponse.Completed, await task.GetResponseAsync());
        await task.WaitAsync();
        await task.CancelAsync();

        var pollCalls = handle.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IScheduledTaskHandle.PollAsync))
            .ToArray();
        Assert.Equal(3, pollCalls.Length);
        Assert.All(pollCalls, call => Assert.Equal(pollingOptions, Assert.IsType<PollingOptions>(call.GetArguments()[0])));
        await handle.Received(2).WaitAsync(Arg.Any<CancellationToken>());
        await handle.Received(1).CancelAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduledGenericDurableTask_AwaitReturnsTypedResult()
    {
        var taskId = TaskId.Create("scheduled-generic");
        var handle = Substitute.For<IScheduledTaskHandle>();
        handle.TaskId.Returns(taskId);
        handle.WaitAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<DurableTaskResponse>(DurableTaskResponse.FromResult(99)));
        ScheduledTask<int> task = new ScheduledDurableTask<int>(handle);

        var result = await task;
        var waited = await task.WaitAsync();

        Assert.Equal(99, result);
        Assert.Equal(99, waited);
        Assert.Equal(taskId, task.Id);
        await handle.Received(2).WaitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StaticCombinators_WaitForAllAndReturnFirstCompleted()
    {
        var first = new CompletedScheduledDurableTask(TaskId.Create("first"), DurableTaskResponse.Completed);
        var second = new CompletedScheduledDurableTask(TaskId.Create("second"), DurableTaskResponse.Completed);
        var firstGeneric = new CompletedScheduledDurableTask<int>(TaskId.Create("first-generic"), DurableTaskResponse.FromResult(1));
        var secondGeneric = new CompletedScheduledDurableTask<int>(TaskId.Create("second-generic"), DurableTaskResponse.FromResult(2));

        await ScheduledTask.WhenAll([first, second]);
        await ScheduledTask.WhenAll([firstGeneric, secondGeneric]);
        var any = await ScheduledTask.WhenAny([first, second]);
        var anyGeneric = await ScheduledTask.WhenAny([firstGeneric, secondGeneric]);

        Assert.Same(first, any);
        Assert.Same(firstGeneric, anyGeneric);
    }
}
