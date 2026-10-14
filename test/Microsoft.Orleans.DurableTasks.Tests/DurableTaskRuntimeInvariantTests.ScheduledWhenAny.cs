using System.Collections.Concurrent;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Verifies that <c>ScheduledTask.WhenAny</c> within a durable execution selects by completion sequence, so a replay in a
/// replacement activation returns the live attempt's winner without a recorded decision.
/// </summary>
public sealed partial class DurableTaskRuntimeInvariantTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public Task ScheduledTaskWhenAnyReplaysTheLiveWinnerWithoutADecisionRecord(int stagedFirst, int stagedSecond) => ActivationScheduler.RunAsync(async () =>
    {
        var manager = new TestStateManager();
        var storage = new TestStorage(manager);
        var (runtime, _, _, _) = CreateRuntime(storage, manager);
        var rootId = TaskId.CreateRoot("root");
        var batchId = rootId.Child("batch");
        var releases = CreateReleases(2);
        var winners = new ConcurrentQueue<TaskId>();
        var firstAttemptSelected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async cancellationToken =>
        {
            var handles = await DurableTask.ScheduleAll(Inputs(releases)).WithId("batch");
            var winner = await ScheduledTask.WhenAny(handles, TestContext.Current.CancellationToken);
            winners.Enqueue(winner.Id);
            if (winners.Count == 1)
            {
                // The first attempt stops here and is aborted, so the next activation replays it.
                firstAttemptSelected.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return winner.GetResult();
        }));

        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => storage.Contains(batchId.Child("1")));

        // Both results commit in one write; the order in which they were staged selects the winner.
        await CommitTogetherAsync(manager, storage, releases, [batchId.Child($"{stagedFirst}"), batchId.Child($"{stagedSecond}")], [stagedFirst, stagedSecond]);
        await firstAttemptSelected.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Both candidates are complete when the replacement activation replays the selection.
        var (replacement, _, _, _) = CreateRuntime(storage, manager);
        await replacement.OnActivateAsync(TestContext.Current.CancellationToken);
        var response = await replacement.GetScheduledTaskHandle(rootId)
            .WaitAsync(TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(10 + stagedFirst, response.GetResult<int>());
        var expected = batchId.Child($"{stagedFirst}");
        Assert.Equal([expected, expected], winners);
        Assert.False(storage.Contains(rootId.Child("$when-any-1").Child("$winner")));
    });
}
