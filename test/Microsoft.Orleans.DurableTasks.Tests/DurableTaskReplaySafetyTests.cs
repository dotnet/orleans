using Orleans.DurableTasks;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Verifies which journal writes commit a workflow's state changes, and what a replay observes after an activation ends.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskReplaySafetyTests
{
    private static readonly TaskId WorkflowId = TaskId.CreateRoot("workflow");

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public Task TailAfterADurableAwaitCommitsItsStateChangesWithTheResult() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(Workflow);
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await activation.ScheduleAsync(WorkflowId, TestCancellation);

            Assert.Equal(1, (await activation.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
            await activation.Grain.UnrelatedWrites;
            AssertTailCommitsWithResult(activation);
        }

        // The next activation recovers the result, so nothing replays the tail.
        await using var recovered = await harness.ActivateAsync(TestCancellation);
        Assert.Equal(1, recovered.Grain.Counter.Value);
        Assert.Equal(1, (await recovered.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
        Assert.Equal(1, harness.EffectRuns);

        static async DurableTask<int> Workflow(ReplayGrain grain)
        {
            await DurableTask.Run(static _ => { }).WithId("first");

            // The tail's state change and a write which other grain work requests.
            grain.Harness.CountEffectRun();
            grain.Counter.Value++;
            grain.RequestUnrelatedWrite();
            return grain.Counter.Value;
        }
    });

    [Fact]
    public Task TailOfAnAsyncDelegateCommitsItsStateChangesWithTheResult() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => DurableTask.Run(async _ =>
        {
            await Task.Yield();
            grain.Counter.Value++;
            grain.RequestUnrelatedWrite();
            return grain.Counter.Value;
        }));
        await using var activation = await harness.ActivateAsync(TestCancellation);

        await activation.ScheduleAsync(WorkflowId, TestCancellation);

        Assert.Equal(1, (await activation.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
        await activation.Grain.UnrelatedWrites;
        AssertTailCommitsWithResult(activation);
    });

    /// <summary>
    /// Asserts that every write which commits the tail's change also commits the workflow's result, so no replay applies
    /// the change again.
    /// </summary>
    private static void AssertTailCommitsWithResult(ReplayActivation activation)
    {
        var tailWrites = activation.Writes.Where(static write => write.Counter == 1).ToArray();
        Assert.NotEmpty(tailWrites);
        Assert.All(tailWrites, static write => Assert.Contains(WorkflowId, write.Completed));
    }
}
