using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Verifies durable transactions: a transaction's changes and its record commit in one journal write, which completes
/// before the transaction returns; a replay returns the recorded outcome without running the body again; and a transaction
/// which aborts cleanly leaves no trace and records its failure.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskTransactionTests
{
    private static readonly TaskId WorkflowId = TaskId.CreateRoot("workflow");

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => TimeSpan.FromSeconds(10);

    [Fact]
    public Task CommittedTransactionReplaysItsResultWithoutRunningItsBody() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => MoveAsync(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await WriteStockAsync(activation.Grain, "a", 5);
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
            Assert.Equal(4, activation.Grain.Stock["a"]);
            Assert.Equal(1, activation.Grain.Stock["b"]);

            // No further write: the transaction returned once the write which holds its changes and record completed.
        }

        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(1, harness.EffectRuns);
        Assert.Equal(4, replay.Grain.Stock["a"]);
        Assert.Equal(1, replay.Grain.Stock["b"]);
        replay.Grain.OpenGate();
        Assert.Equal(1, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
    });

    [Fact]
    public Task TransactionWhoseCommitWriteFailsRunsAgain() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => MoveAfterGateAsync(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await WriteStockAsync(activation.Grain, "a", 5);
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);

            // The commit's write fails, so neither the changes nor the record reach the journal.
            activation.FailWrites();
            activation.Grain.OpenGate();
            await activation.FailedWrite.WaitAsync(Timeout, TestCancellation);
        }

        // The replay runs the body once more, against the committed state.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(2, harness.EffectRuns);
        Assert.Equal(4, replay.Grain.Stock["a"]);
        replay.Grain.OpenGate();
        Assert.Equal(1, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());
        Assert.Equal(1, replay.Grain.Stock["b"]);

        static async DurableTask<int> MoveAfterGateAsync(ReplayGrain grain) =>
            await DurableTaskTransactions.RunAsync("move", async _ =>
            {
                grain.Harness.CountEffectRun();
                grain.Stock["a"] -= 1;
                await grain.WaitAtPlainGateAsync();
                grain.Stock["b"] = 1;
                return 1;
            });
    });

    [Fact]
    public Task FailedTransactionLeavesNoTraceAndReplaysItsFailure() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => FailAsync(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await WriteStockAsync(activation.Grain, "a", 5);
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
            Assert.Equal(5, activation.Grain.Stock["a"]);
            Assert.False(activation.Grain.Stock.ContainsKey("b"));
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;
        }

        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(1, harness.EffectRuns);
        Assert.Equal(5, replay.Grain.Stock["a"]);
        replay.Grain.OpenGate();
        Assert.Equal("insufficient", (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<string>());

        static async DurableTask<string> FailAsync(ReplayGrain grain)
        {
            string outcome;
            try
            {
                await DurableTaskTransactions.RunAsync("move", async _ =>
                {
                    grain.Harness.CountEffectRun();
                    grain.Stock["a"] -= 1;
                    grain.Stock["b"] = 1;
                    await Task.Yield();
                    throw new InvalidOperationException("insufficient");
#pragma warning disable CS0162 // Unreachable code detected
                    return 0;
#pragma warning restore CS0162
                });
                outcome = "moved";
            }
            catch (InvalidOperationException exception)
            {
                outcome = exception.Message;
            }

            await grain.WaitAtGateAsync();
            return outcome;
        }
    });

    private static async DurableTask<int> MoveAsync(ReplayGrain grain)
    {
        var moved = await DurableTaskTransactions.RunAsync("move", async _ =>
        {
            grain.Harness.CountEffectRun();
            grain.Stock["a"] -= 1;
            await Task.Yield();
            grain.Stock["b"] = grain.Stock.TryGetValue("b", out var b) ? b + 1 : 1;
            return grain.Stock["b"];
        });

        await grain.WaitAtGateAsync();
        return moved;
    }

    private static async Task WriteStockAsync(ReplayGrain grain, string item, int level)
    {
        grain.Stock[item] = level;
        grain.RequestUnrelatedWrite();
        await grain.UnrelatedWrites;
    }
}
