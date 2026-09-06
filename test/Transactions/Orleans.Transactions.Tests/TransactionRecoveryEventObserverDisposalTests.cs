using System.Diagnostics;
using Orleans.Transactions.Diagnostics;
using Orleans.Transactions.TestKit;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class TransactionRecoveryEventObserverDisposalTests
{
    private static readonly TimeSpan SynchronizationTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Dispose_ReleasesReachedPhaseGate()
    {
        var resource = CreateParticipant("manager", ParticipantId.Role.Manager);
        var transactionId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow;
        var observer = new TransactionRecoveryEventObserver(candidate => candidate.Name == resource.Name);
        var gate = observer.GateNextTransition(transition =>
            transition.Kind == TransactionRecoveryEventObserver.RecoveryTransitionKind.TransactionManagerWaitingForPrepared);
        var deadline = GetDeadline();
        var emission = RunLongRunning(() => TransactionDiagnosticEvents.EmitTransactionManagerWaitingForPrepared(
            resource,
            transactionId,
            timestamp,
            waitCount: 2,
            deadline: timestamp.AddSeconds(10)));

        try
        {
            var transition = await gate.WaitAsync(deadline, TestContext.Current.CancellationToken);

            Assert.False(emission.IsCompleted);
            Assert.Equal(transactionId, transition.TransactionId);
            Assert.Equal(TransactionDiagnosticEvents.TransactionPhase.WaitingForRemotePrepares, transition.Phase);

            observer.Dispose();

            await WaitBeforeDeadlineAsync(emission, deadline, "observer disposal to release the reached phase gate");
        }
        finally
        {
            gate.Dispose();
            observer.Dispose();
            await WaitBeforeDeadlineAsync(
                emission,
                GetDeadline(),
                "reached phase gate cleanup");
        }
    }

    [Fact]
    public async Task Dispose_ReleasesArmedPhaseGate()
    {
        var observer = new TransactionRecoveryEventObserver(_ => true);
        var gate = observer.GateNextTransition(_ => true);
        var blockStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingTask = RunLongRunning(() =>
        {
            blockStarted.TrySetResult();
            gate.Block();
        });

        try
        {
            await WaitBeforeDeadlineAsync(blockStarted.Task, GetDeadline(), "the armed phase gate to begin blocking");
            Assert.False(blockingTask.IsCompleted);

            observer.Dispose();

            await WaitBeforeDeadlineAsync(blockingTask, GetDeadline(), "observer disposal to release the armed phase gate");
            Assert.False(gate.TryReach(null!));
        }
        finally
        {
            gate.Dispose();
            observer.Dispose();
            await WaitBeforeDeadlineAsync(blockingTask, GetDeadline(), "armed phase gate cleanup");
        }
    }

    [Fact]
    public async Task Dispose_WhenRepeated_IsIdempotent()
    {
        var observer = new TransactionRecoveryEventObserver(_ => true);
        var gate = observer.GateNextTransition(_ => true);
        var waiter = observer.WaitForNextTransitionAsync(
            observer.LatestRelevantSequence,
            GetDeadline(),
            TestContext.Current.CancellationToken);

        observer.Dispose();
        observer.Dispose();
        gate.Dispose();
        gate.Dispose();

        var exception = await Assert.ThrowsAsync<ObjectDisposedException>(() => waiter);
        Assert.Equal(nameof(TransactionRecoveryEventObserver), exception.ObjectName);
        Assert.False(gate.TryReach(null!));
    }

    private static ParticipantId CreateParticipant(string name, ParticipantId.Role role)
        => new($"{name}-{Guid.NewGuid():N}", reference: null!, role);

    private static long GetDeadline()
        => Stopwatch.GetTimestamp() + (long)(SynchronizationTimeout.TotalSeconds * Stopwatch.Frequency);

    private static Task RunLongRunning(Action action)
        => Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private static async Task WaitBeforeDeadlineAsync(Task task, long deadline, string phase)
    {
        var now = Stopwatch.GetTimestamp();
        if (now >= deadline)
        {
            throw new TimeoutException($"Timed out waiting for {phase}; task status is {task.Status}.");
        }

        try
        {
            await task.WaitAsync(
                Stopwatch.GetElapsedTime(now, deadline),
                TestContext.Current.CancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"Timed out waiting for {phase}; task status is {task.Status}.",
                exception);
        }
    }
}
