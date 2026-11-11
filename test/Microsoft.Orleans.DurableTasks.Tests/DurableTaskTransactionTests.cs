using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
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

    [Fact]
    public Task TransactionDoomedByANestedFailureRecordsItsFailure() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => DoomAsync(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await WriteStockAsync(activation.Grain, "a", 5);
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
            Assert.Equal(5, activation.Grain.Stock["a"]);
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;
        }

        // The replay throws the recorded failure without running the body again.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(1, harness.EffectRuns);
        Assert.Equal(5, replay.Grain.Stock["a"]);
        replay.Grain.OpenGate();
        var outcome = (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<string>();
        Assert.StartsWith(nameof(InvalidOperationException), outcome, StringComparison.Ordinal);

        static async DurableTask<string> DoomAsync(ReplayGrain grain)
        {
            string outcome;
            try
            {
                await DurableTaskTransactions.RunAsync("move", async cancellationToken =>
                {
                    grain.Harness.CountEffectRun();
                    grain.Stock["a"] -= 1;
                    try
                    {
                        await grain.Manager.RunTransactionAsync(_ => throw new FormatException("nested"), cancellationToken);
                    }
                    catch (FormatException)
                    {
                    }

                    return 0;
                });
                outcome = "moved";
            }
            catch (Exception exception)
            {
                outcome = $"{exception.GetType().Name}: {exception.Message}";
            }

            await grain.WaitAtGateAsync();
            return outcome;
        }
    });

    [Fact]
    public Task TransactionBodyRunsCoveredSoLaterIdentifiersDontShift() => ActivationScheduler.RunAsync(async () =>
    {
        await using var harness = new DurableTaskReplayHarness(static grain => Workflow(grain));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await WriteStockAsync(activation.Grain, "a", 5);
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
            Assert.Equal(4, activation.Grain.Stock["a"]);
            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;
        }

        // The body's effect ran directly and its durable operation threw, so neither took an identifier: the replay, which
        // skips the body, returns the transaction's record and the later unnamed effect's record without running either.
        Assert.Equal(2, harness.EffectRuns);
        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(2, harness.EffectRuns);
        Assert.Equal(4, replay.Grain.Stock["a"]);
        replay.Grain.OpenGate();
        Assert.Equal("7 rejected 9", (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<string>());

        static async DurableTask<string> Workflow(ReplayGrain grain)
        {
            var (inner, rejected) = await DurableTaskTransactions.RunAsync("move", async _ =>
            {
                grain.Harness.CountEffectRun();
                grain.Stock["a"] -= 1;
                await Task.Yield();
                var inner = DurableTask.RunInline(static () => 7);
                var rejected = Record.Exception(static () => DurableTask.Run(static _ => { }).WithId("covered").Post()) is InvalidOperationException;
                return (inner, rejected);
            });

            var after = DurableTask.RunInline(() =>
            {
                grain.Harness.CountEffectRun();
                return 9;
            });

            await grain.WaitAtGateAsync();
            return $"{inner} {(rejected ? "rejected" : "posted")} {after}";
        }
    });

    [Fact]
    public Task TransactionInADiscardedAttemptAbortsAndRunsAgain() => ActivationScheduler.RunAsync(async () =>
    {
        var outcome = new TaskCompletionSource<(Exception? Failure, int Stock)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, outcome));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await WriteStockAsync(activation.Grain, "a", 5);
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);

            // Stopping the activation discards the attempt, whose transaction body ignores the cancellation and returns.
        }

        // The discarded attempt's commit aborts: its changes are rolled back and nothing is recorded.
        var (failure, stock) = await outcome.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(DurableTaskAttemptCancellationReason.ActivationStopping, Assert.IsType<DurableTaskAttemptCanceledException>(failure).Reason);
        Assert.Equal(5, stock);

        await using var replay = await harness.ActivateAsync(TestCancellation);
        await replay.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(2, harness.EffectRuns);
        Assert.Equal(4, replay.Grain.Stock["a"]);
        replay.Grain.OpenGate();
        Assert.Equal(1, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).GetResult<int>());

        static async DurableTask<int> Workflow(ReplayGrain grain, TaskCompletionSource<(Exception?, int)> outcome)
        {
            try
            {
                return await DurableTaskTransactions.RunAsync("move", async _ =>
                {
                    grain.Harness.CountEffectRun();
                    grain.Stock["a"] -= 1;
                    try
                    {
                        await grain.WaitAtGateAsync();
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    return 1;
                });
            }
            catch (Exception exception)
            {
                outcome.TrySetResult((exception, grain.Stock["a"]));
                throw;
            }
        }
    });

    [Fact]
    public Task TransactionInAnAbortedTasksCleanupCommitsLiveWithoutARecord() => ActivationScheduler.RunAsync(async () =>
    {
        var cleanup = new TaskCompletionSource<(Exception? Failure, int Result)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, cleanup));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await WriteStockAsync(activation.Grain, "a", 5);
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);
            await ((IDurableTaskManager)activation.Runtime).AbortAsync(WorkflowId, "operator", TestCancellation);

            // The task never replays, so the cleanup's transaction commits its changes as the cleanup's other writes apply,
            // and records nothing beside the effect which the body recorded before the abort.
            Assert.Equal((null, 1), await cleanup.Task.WaitAsync(Timeout, TestCancellation));
            Assert.Equal(4, activation.Grain.Stock["a"]);
            Assert.Single(activation.Storage.GetChildren(WorkflowId));
        }

        // The transaction returned once its write completed: the next activation, which the activation ended without a
        // further write, holds its changes, and doesn't run the aborted task again.
        await using var replay = await harness.ActivateAsync(TestCancellation);
        Assert.Equal(DurableTaskStatus.Failed, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).Status);
        Assert.Equal(1, harness.EffectRuns);
        Assert.Equal(4, replay.Grain.Stock["a"]);
        Assert.Equal(1, replay.Grain.Stock["b"]);
        Assert.Single(replay.Storage.GetChildren(WorkflowId));

        static async DurableTask Workflow(ReplayGrain grain, TaskCompletionSource<(Exception?, int)> cleanup)
        {
            DurableTask.RunInline(static () => 0);
            try
            {
                await grain.WaitAtGateAsync();
            }
            catch (OperationCanceledException)
            {
                cleanup.TrySetResult(await RunMoveAsync(grain));
                throw;
            }
        }
    });

    [Fact]
    public Task TransactionAfterTaskCancellationCommitsWithItsRecord() => ActivationScheduler.RunAsync(async () =>
    {
        var gates = new CleanupGates();
        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, gates));
        await using (var activation = await harness.ActivateAsync(TestCancellation))
        {
            await WriteStockAsync(activation.Grain, "a", 5);
            await activation.ScheduleAsync(WorkflowId, TestCancellation);
            await activation.Grain.AtGate.WaitAsync(Timeout, TestCancellation);

            // A request to cancel the task is recorded, and the attempt keeps recording: the cleanup's transaction commits
            // with its record, beside the effect which the body recorded before the request.
            await ((IDurableTaskManager)activation.Runtime).CancelAsync(WorkflowId, "buyer", TestCancellation);
            Assert.Equal((null, 1), await gates.Committed.Task.WaitAsync(Timeout, TestCancellation));
            Assert.Equal(4, activation.Grain.Stock["a"]);

            // The effect, the point at which the body observed the cancellation, and the transaction's record.
            Assert.Equal(3, activation.Storage.GetChildren(WorkflowId).Count());
            Assert.True(activation.Storage.TryGetTask(WorkflowId.Child(DurableTaskGrainRuntime.CancellationPointSegment), out _));

            // The activation ends while the cleanup waits, without a further write.
        }

        // The replay observes the recorded cancellation and returns the transaction's record without running its body.
        gates = new CleanupGates();
        await using var replay = await harness.ActivateAsync(TestCancellation);
        Assert.Equal((null, 1), await gates.Committed.Task.WaitAsync(Timeout, TestCancellation));
        Assert.Equal(1, harness.EffectRuns);
        Assert.Equal(4, replay.Grain.Stock["a"]);
        Assert.Equal(1, replay.Grain.Stock["b"]);
        gates.Release.SetResult();
        Assert.Equal(DurableTaskStatus.Canceled, (await replay.GetResponseAsync(WorkflowId, TestCancellation)).Status);

        static async DurableTask Workflow(ReplayGrain grain, CleanupGates gates)
        {
            DurableTask.RunInline(static () => 0);
            try
            {
                await grain.WaitAtGateAsync();
            }
            catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
            {
                gates.Committed.TrySetResult(await RunMoveAsync(grain));
                await gates.Release.Task.WaitAsync(DurableTask.Current.AttemptCancellationToken);
                throw;
            }
        }
    });

    /// <summary>Runs the transaction which moves one item from <c>a</c> to <c>b</c>, and returns its outcome.</summary>
    private static async Task<(Exception? Failure, int Result)> RunMoveAsync(ReplayGrain grain)
    {
        try
        {
            return (null, await DurableTaskTransactions.RunAsync("move", async _ =>
            {
                grain.Harness.CountEffectRun();
                grain.Stock["a"] -= 1;
                await Task.Yield();
                grain.Stock["b"] = 1;
                return 1;
            }));
        }
        catch (Exception exception)
        {
            return (exception, 0);
        }
    }

    private sealed class CleanupGates
    {
        public TaskCompletionSource<(Exception? Failure, int Result)> Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

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
