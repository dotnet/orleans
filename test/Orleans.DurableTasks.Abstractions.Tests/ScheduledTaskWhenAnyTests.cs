using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

/// <summary>Verifies that <c>ScheduledTask.WhenAny</c> selects through the host within a durable execution.</summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public class ScheduledTaskWhenAnyTests
{
    private static readonly TaskId RootId = TaskId.CreateRoot("root");

    private static CancellationToken TestCancellation => Xunit.TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithinAnExecutionTheHostSelectsTheWinnerAndAReplayReturnsTheSameTask(bool generic)
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var releases = Enumerable.Range(0, 2)
            .Select(static _ => new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var winners = new System.Collections.Concurrent.ConcurrentQueue<TaskId>();
        var definition = DurableTask.Run(async _ =>
        {
            var handles = await DurableTask.ScheduleAll(releases.Select(static release => DurableTask.Run(async _ => await release.Task)))
                .WithId("batch");
            var winner = generic
                ? await ScheduledTask.WhenAny(handles, TestCancellation)
                : await ScheduledTask.WhenAny((IReadOnlyList<ScheduledTask>)handles, TestCancellation);
            Assert.True(winner.IsCompleted);
            winners.Enqueue(winner.Id);
        });

        var live = DurableTaskRuntimeHelper.RunAsync(definition, host.CreateContext(RootId)).AsTask();
        releases[1].SetResult(1);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, (await live.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation)).Status);

        // Both candidates are complete when the execution replays; the recorded selection returns the same task.
        releases[0].SetResult(0);
        var replay = await DurableTaskRuntimeHelper.RunAsync(definition, host.CreateContext(RootId));
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, replay.Status);

        var expected = TaskId.Parse("root/batch/1");
        Assert.Equal([expected, expected], winners);
        var decisionId = TaskId.Parse("root/$when-any-1/$winner");
        Assert.Equal([decisionId, decisionId], host.DecisionIds);
    }

    [Fact]
    public async Task WithinAnExecutionCallerCancellationAbandonsTheSelection()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var root = host.CreateRootDefinition<bool>(async _ =>
        {
            var handles = await DurableTask.ScheduleAll([DurableTask.Run(async _ => await release.Task)]);
            var selection = ScheduledTask.WhenAny(handles, cancellation.Token);
            Assert.False(selection.IsCompleted);
            await cancellation.CancelAsync();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);
            release.SetResult(1);
            return DurableTaskResponse.FromResult(exception.CancellationToken == cancellation.Token);
        });

        Assert.True(await await root.ScheduleAsync("root", TestCancellation));
        Assert.Equal([TaskId.Parse("root/$when-any-2/$winner")], host.DecisionIds);
    }

    [Fact]
    public async Task OutsideAnExecutionTheFirstObservedResponseWinsWithoutADecision()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = await host.CreateRootDefinition<int>(async _ => DurableTaskResponse.FromResult(await release.Task))
            .ScheduleAsync("pending", TestCancellation);
        var completed = await host.CreateRootDefinition<int>(static _ => ValueTask.FromResult<DurableTaskResponse>(DurableTaskResponse.FromResult(2)))
            .ScheduleAsync("completed", TestCancellation);

        var winner = await ScheduledTask.WhenAny([pending, completed], TestCancellation);

        Assert.Same(completed, winner);
        Assert.True(winner.IsCompleted);
        Assert.Empty(host.DecisionIds);
        release.SetResult(1);
    }

    [Fact]
    public async Task EmptyCandidatesAreRejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ScheduledTask.WhenAny(Array.Empty<ScheduledTask>(), TestCancellation));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ScheduledTask.WhenAny(Array.Empty<ScheduledTask<int>>(), TestCancellation));
    }
}
