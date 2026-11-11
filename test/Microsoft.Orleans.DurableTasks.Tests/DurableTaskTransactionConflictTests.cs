using Orleans.DurableTasks;
using Orleans.Journaling;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Verifies how durable task bodies meet transactions over the grain's journaled state which run alongside them: a recorded
/// read or write of a state which an open transaction holds stalls the body's attempt without recording the access, a new
/// attempt runs once the transaction ends and observes its outcome, and a replayed access returns its record without
/// meeting the transaction.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskTransactionConflictTests
{
    private static readonly TaskId WorkflowId = TaskId.CreateRoot("workflow");

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task BodyWhichMeetsAHeldStateStallsAndRunsAgainOnceTheTransactionEnds(bool writes, bool abort) => ActivationScheduler.RunAsync(async () =>
    {
        var attempts = 0;
        var conflict = new TaskCompletionSource<DurableTaskAttemptCancellationReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain));
        await using var activation = await harness.ActivateAsync(TestCancellation);
        await WriteStockAsync(activation.Grain, "a", 5);
        await activation.ScheduleAsync(WorkflowId, TestCancellation);
        await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);

        // A transaction holds the state while the body waits; the body then meets it.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transaction = activation.Grain.Manager.RunTransactionAsync(async _ =>
        {
            activation.Grain.Stock["a"] = 10;
            await release.Task;
            if (abort)
            {
                throw new InvalidOperationException("abort");
            }
        }, TestCancellation);
        activation.Grain.OpenGate();
        Assert.Equal(DurableTaskAttemptCancellationReason.TransactionConflict, await conflict.Task.WaitAsync(Timeout, TestCancellation));
        Assert.Equal(1, attempts);

        // The next attempt runs once the transaction ends, against its outcome.
        release.SetResult();
        if (abort)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction);
        }
        else
        {
            await transaction;
        }

        var expected = writes ? 7 : abort ? 5 : 10;
        Assert.Equal(expected, (await activation.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
        Assert.Equal(2, attempts);
        Assert.Equal(expected, activation.Grain.Stock["a"]);

        async DurableTask<int> Workflow(ReplayGrain grain)
        {
            attempts++;
            await grain.WaitAtPlainGateAsync();
            try
            {
                if (writes)
                {
                    grain.Stock["a"] = 7;
                }

                return grain.Stock["a"];
            }
            catch (DurableTaskAttemptCanceledException canceled)
            {
                conflict.TrySetResult(canceled.Reason);
                throw;
            }
        }
    });

    [Fact]
    public Task ReplayedAccessReturnsItsRecordWithoutMeetingAnOpenTransaction() => ActivationScheduler.RunAsync(async () =>
    {
        var observed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var conflicts = 0;
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await WriteStockAsync(activation.Grain, "a", 5);
            activation.Grain.OpenGate();
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            Assert.Equal(5, await observed.Task.WaitAsync(Timeout, TestCancellation));

            // The write commits the read's record; the activation then ends while the body waits.
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;
        }

        // The next activation replays the body while a transaction holds the state which the body read.
        observed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transaction = replay.Grain.Manager.RunTransactionAsync(async _ =>
        {
            replay.Grain.Stock["a"] = 10;
            await release.Task;
        }, TestCancellation);
        replay.Grain.OpenGate();

        Assert.Equal(5, await observed.Task.WaitAsync(Timeout, TestCancellation));
        Assert.Equal(0, conflicts);
        Assert.NotNull(replay.Grain.Manager.OpenTransaction);
        release.SetResult();
        await transaction;

        async DurableTask<int> Workflow(ReplayGrain grain)
        {
            await grain.WaitAtPlainGateAsync();
            int value;
            try
            {
                value = grain.Stock["a"];
            }
            catch (DurableTaskAttemptCanceledException)
            {
                conflicts++;
                throw;
            }

            observed.TrySetResult(value);
            await Task.Delay(Timeout, DurableTask.Current.CancellationToken);
            return value;
        }
    });

    [Fact]
    public Task RuntimeBookkeepingDuringAnOpenTransactionSurvivesItsAbort() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await WriteStockAsync(activation.Grain, "a", 5);
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);

            // While a transaction holds the grain's state, the runtime stores the task's result, its own bookkeeping, which
            // doesn't pass the transaction's guard and commits with a write which the transaction doesn't hold back.
            await Assert.ThrowsAsync<InvalidOperationException>(() => activation.Grain.Manager.RunTransactionAsync(async _ =>
            {
                activation.Grain.Stock["a"] = 10;
                activation.Grain.OpenGate();
                Assert.Equal(1, (await activation.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
                throw new InvalidOperationException("abort");
            }, TestCancellation));

            Assert.Equal(5, activation.Grain.Stock["a"]);
        }

        // The result survived the abort.
        await using var recovered = await harness.ActivateAsync(TestCancellation);
        Assert.Equal(1, (await recovered.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
        Assert.Equal(5, recovered.Grain.Stock["a"]);

        static async DurableTask<int> Workflow(ReplayGrain grain)
        {
            await grain.WaitAtPlainGateAsync();
            return 1;
        }
    });

    private static async Task WriteStockAsync(ReplayGrain grain, string item, int level)
    {
        grain.Stock[item] = level;
        grain.RequestUnrelatedWrite();
        await grain.UnrelatedWrites;
    }
}
