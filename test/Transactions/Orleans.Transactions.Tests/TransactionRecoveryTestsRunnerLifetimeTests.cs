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
public class TransactionRecoveryTestsRunnerLifetimeTests
{
    private static readonly TimeSpan SynchronizationTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ProducerCancellationScope_CompletedProducerDisposesCancellationSource()
    {
        var stopProducing = new CancellationTokenSource();
        var scope = new TransactionRecoveryTestsRunner.ProducerCancellationScope(
            stopProducing,
            Task.CompletedTask);

        await scope.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => stopProducing.Token);
    }

    [Fact]
    public async Task ProducerCancellationScope_FaultedProducerDoesNotThrowDuringDisposal()
    {
        var stopProducing = new CancellationTokenSource();
        var scope = new TransactionRecoveryTestsRunner.ProducerCancellationScope(
            stopProducing,
            Task.FromException(new InvalidOperationException("boom")));

        await scope.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => stopProducing.Token);
    }

    [Fact]
    public async Task ProducerCancellationScope_ActiveProducerIsAwaitedBeforeCancellationSourceDisposal()
    {
        var stopProducing = new CancellationTokenSource();
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = stopProducing.Token.Register(() => cancellationObserved.TrySetResult());
        var producer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scope = new TransactionRecoveryTestsRunner.ProducerCancellationScope(
            stopProducing,
            producer.Task);

        var disposal = scope.DisposeAsync().AsTask();
        await cancellationObserved.Task.WaitAsync(SynchronizationTimeout, TestContext.Current.CancellationToken);

        Assert.False(disposal.IsCompleted);
        Assert.True(stopProducing.Token.IsCancellationRequested);

        producer.TrySetResult();
        await disposal.WaitAsync(SynchronizationTimeout, TestContext.Current.CancellationToken);

        Assert.Throws<ObjectDisposedException>(() => stopProducing.Token);
    }

    [Fact]
    public async Task ObserveAndReleaseGateAsync_ReachedGateCompletesBeforeDisposal()
    {
        var observer = new TransactionRecoveryEventObserver(_ => true);
        var gate = observer.GateNextTransition(_ => true);
        var transition = CreateTransition();
        var observation = TransactionRecoveryTestsRunner.ObserveAndReleaseGateAsync(
            gate,
            GetDeadline(),
            TestContext.Current.CancellationToken);
        var blockingTask = RunLongRunning(() =>
        {
            Assert.True(gate.TryReach(transition));
            gate.Block();
        });

        try
        {
            Assert.Same(transition, await observation.WaitAsync(
                SynchronizationTimeout,
                TestContext.Current.CancellationToken));
            await blockingTask.WaitAsync(SynchronizationTimeout, TestContext.Current.CancellationToken);
        }
        finally
        {
            gate.Dispose();
            observer.Dispose();
            await blockingTask.WaitAsync(SynchronizationTimeout, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ObserveAndReleaseGateAsync_CancellationCompletesBeforeDisposal()
    {
        var observer = new TransactionRecoveryEventObserver(_ => true);
        var gate = observer.GateNextTransition(_ => true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var observation = TransactionRecoveryTestsRunner.ObserveAndReleaseGateAsync(
            gate,
            GetDeadline(),
            cancellation.Token);
        var blockingTask = RunLongRunning(gate.Block);

        try
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observation);
            await blockingTask.WaitAsync(SynchronizationTimeout, TestContext.Current.CancellationToken);
        }
        finally
        {
            gate.Dispose();
            observer.Dispose();
            await blockingTask.WaitAsync(SynchronizationTimeout, TestContext.Current.CancellationToken);
        }
    }

    private static TransactionRecoveryEventObserver.RecoveryTransition CreateTransition() =>
        new(
            Sequence: 1,
            ObservedAtUtc: DateTime.UtcNow,
            Elapsed: TimeSpan.Zero,
            Kind: TransactionRecoveryEventObserver.RecoveryTransitionKind.TransactionConfirmCompleted,
            TransactionIds: [],
            ProtocolRole: TransactionDiagnosticEvents.TransactionProtocolRole.RemoteParticipant,
            Phase: TransactionDiagnosticEvents.TransactionPhase.Confirm,
            ResourceName: "test",
            GrainId: null,
            SiloAddress: null,
            ActivationId: default,
            Status: null,
            CommitCount: null,
            Succeeded: true);

    private static long GetDeadline()
        => Stopwatch.GetTimestamp() + (long)(SynchronizationTimeout.TotalSeconds * Stopwatch.Frequency);

    private static Task RunLongRunning(Action action)
        => Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
}
