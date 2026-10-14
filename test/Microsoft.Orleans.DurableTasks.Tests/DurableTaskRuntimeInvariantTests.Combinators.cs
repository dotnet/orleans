using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

public sealed partial class DurableTaskRuntimeInvariantTests
{
    [Fact]
    public Task NamedWhenAllWaitsForEveryInputThenThrowsTheFirstFailureInInputOrder() => ActivationScheduler.RunAsync(async () =>
    {
        var (runtime, storage, _, _) = CreateRuntime();
        var rootId = TaskId.CreateRoot("root");
        var request = CreateRequest(1, () => DurableTask.Run(async _ =>
        {
            try
            {
                await DurableTask.WhenAll(
                [
                    DurableTask.Run(static _ => 1),
                    DurableTask.Run((Func<CancellationToken, int>)(static _ => throw new OperationCanceledException("canceled input"))),
                    DurableTask.Run((Func<CancellationToken, int>)(static _ => throw new InvalidOperationException("first failure"))),
                    DurableTask.Run((Func<CancellationToken, int>)(static _ => throw new InvalidOperationException("second failure"))),
                ]).WithId("batch");
                return "completed";
            }
            catch (InvalidOperationException exception)
            {
                return exception.Message;
            }
        }));

        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        var response = await runtime.GetScheduledTaskHandle(rootId)
            .WaitAsync(TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("first failure", response.GetResult<string>());
        Assert.Equal(
            [DurableTaskStatus.CompletedSuccessfully, DurableTaskStatus.Canceled, DurableTaskStatus.Failed, DurableTaskStatus.Failed],
            Enumerable.Range(0, 4).Select(index => storage.Get(rootId.Child("batch").Child($"{index}")).Result!.Status));

        // The combinator runs as part of the root, so it stores no task of its own.
        Assert.False(storage.Contains(rootId.Child("batch")));
    });

    [Fact]
    public Task NamedWhenAllReplaysRecordedResultsWhenAnEarlierCombinatorIsAdded() => ActivationScheduler.RunAsync(async () =>
    {
        var manager = new TestStateManager();
        var storage = new TestStorage(manager);
        var (runtime, _, _, _) = CreateRuntime(storage, manager);
        var rootId = TaskId.CreateRoot("root");
        var runs = new int[2];
        var attempts = 0;
        var addEarlierCombinator = false;
        var firstAttemptCompletedBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(async cancellationToken =>
        {
            if (addEarlierCombinator)
            {
                await DurableTask.WhenAll([DurableTask.Run(static _ => { })]);
            }

            var results = await DurableTask.WhenAll(
            [
                DurableTask.Run(_ => Interlocked.Increment(ref runs[0])),
                DurableTask.Run(_ => Interlocked.Increment(ref runs[1]) * 10),
            ]).WithId("batch");
            if (Interlocked.Increment(ref attempts) == 1)
            {
                // The first attempt stops here and is aborted, so the next activation replays it.
                firstAttemptCompletedBatch.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return results.Sum();
        }));

        await ((IDurableTaskServer)runtime).ScheduleAsync(rootId, request, TestContext.Current.CancellationToken);
        await firstAttemptCompletedBatch.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await runtime.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // A deployment adds a combinator before the named one; the named inputs keep their identifiers.
        addEarlierCombinator = true;
        var (replacement, _, _, _) = CreateRuntime(storage, manager);
        await replacement.OnActivateAsync(TestContext.Current.CancellationToken);
        var response = await replacement.GetScheduledTaskHandle(rootId)
            .WaitAsync(TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(11, response.GetResult<int>());
        Assert.Equal([1, 1], runs);
        Assert.Equal(2, attempts);
        Assert.True(storage.Contains(rootId.Child("$when-all-1").Child("0")));
        Assert.Equal(1, storage.Get(rootId.Child("batch").Child("0")).Result!.GetResult<int>());
        Assert.Equal(10, storage.Get(rootId.Child("batch").Child("1")).Result!.GetResult<int>());
    });
}
