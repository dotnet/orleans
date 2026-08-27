using System.Diagnostics;
using Orleans.Transactions.TestKit;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class TransactionRecoveryFailureObservationTests
{
    [Theory]
    [InlineData(true, 0, 45)]
    [InlineData(false, 30, 15)]
    public void GetTimeouts_PreservesAbsoluteWatchdogWhenAllocatingProducerDrain(
        bool gracefulShutdown,
        int expectedObservationWindowSeconds,
        int expectedProducerDrainSeconds)
    {
        var clientResponseTimeout = TimeSpan.FromSeconds(30);
        var schedulingMargin = TimeSpan.FromSeconds(15);

        var timeouts = TransactionRecoveryFailureObservation.GetTimeouts(
            gracefulShutdown,
            clientResponseTimeout,
            schedulingMargin);

        Assert.Equal(TimeSpan.FromSeconds(expectedObservationWindowSeconds), timeouts.ObservationWindow);
        Assert.Equal(TimeSpan.FromSeconds(expectedProducerDrainSeconds), timeouts.ProducerDrainTimeout);
        Assert.Equal(TimeSpan.FromSeconds(45), timeouts.MaximumDuration);
    }

    [Fact]
    public async Task DetectAsync_NonCancellableProducerTimesOutAndStopsProducing()
    {
        var producer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopProducing = new CancellationTokenSource();
        var responseWindow = TimeSpan.FromMilliseconds(25);
        var schedulingMargin = TimeSpan.FromMilliseconds(25);

        var outcome = await TransactionRecoveryFailureObservation.DetectAsync(
            producer.Task,
            failure.Task,
            stopProducing,
            responseWindow,
            schedulingMargin,
            TestContext.Current.CancellationToken);

        Assert.Equal(TransactionRecoveryFailureObservation.OutcomeKind.AttemptTimedOut, outcome.Kind);
        Assert.Null(outcome.Failure);
        Assert.Equal(0, outcome.ProducerResult);
        Assert.False(outcome.ProducerSettled);
        Assert.True(stopProducing.IsCancellationRequested);
        Assert.False(producer.Task.IsCompleted);
        Assert.True(outcome.Elapsed >= responseWindow + schedulingMargin);
        Assert.InRange(
            outcome.Elapsed,
            responseWindow + schedulingMargin,
            responseWindow + schedulingMargin + TimeSpan.FromSeconds(5));
        Assert.InRange(outcome.DrainElapsed, TimeSpan.Zero, schedulingMargin + TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DetectAsync_FailureAtWatchdogBoundaryWinsOverSettledProducer()
    {
        var producer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expectedFailure = new InvalidOperationException("Failure at the watchdog boundary");
        using var stopProducing = new CancellationTokenSource();
        using var registration = stopProducing.Token.Register(
            () =>
            {
                producer.TrySetResult(73);
                failure.TrySetResult(expectedFailure);
            });

        var outcome = await TransactionRecoveryFailureObservation.DetectAsync(
            producer.Task,
            failure.Task,
            stopProducing,
            observationWindow: TimeSpan.Zero,
            producerDrainTimeout: TimeSpan.FromSeconds(1),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TransactionRecoveryFailureObservation.OutcomeKind.FailureObserved, outcome.Kind);
        Assert.Same(expectedFailure, outcome.Failure);
        Assert.Equal(73, outcome.ProducerResult);
        Assert.True(outcome.ProducerSettled);
        Assert.True(stopProducing.IsCancellationRequested);
        Assert.True(producer.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DetectAsync_CancellationBetweenAttemptsStartsNoNextAttempt()
    {
        var firstAttemptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var betweenAttempts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopProducing = new CancellationTokenSource();
        using var registration = stopProducing.Token.Register(() => cancellationObserved.TrySetResult());
        var attemptCount = 0;
        var activeAttempts = 0;
        var maximumActiveAttempts = 0;

        async Task<int> ProduceAsync()
        {
            while (!stopProducing.IsCancellationRequested)
            {
                var active = Interlocked.Increment(ref activeAttempts);
                maximumActiveAttempts = Math.Max(maximumActiveAttempts, active);
                Interlocked.Increment(ref attemptCount);
                firstAttemptStarted.TrySetResult();
                await finishFirstAttempt.Task.WaitAsync(TestContext.Current.CancellationToken);
                Interlocked.Decrement(ref activeAttempts);

                betweenAttempts.TrySetResult();
                await cancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
            }

            return attemptCount;
        }

        var producer = ProduceAsync();
        await firstAttemptStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        finishFirstAttempt.TrySetResult();
        await betweenAttempts.Task.WaitAsync(TestContext.Current.CancellationToken);

        var outcome = await TransactionRecoveryFailureObservation.DetectAsync(
            producer,
            failure.Task,
            stopProducing,
            observationWindow: TimeSpan.Zero,
            producerDrainTimeout: TimeSpan.FromSeconds(1),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TransactionRecoveryFailureObservation.OutcomeKind.StoppedWithoutFailure, outcome.Kind);
        Assert.Null(outcome.Failure);
        Assert.Equal(1, outcome.ProducerResult);
        Assert.True(outcome.ProducerSettled);
        Assert.True(producer.IsCompletedSuccessfully);
        Assert.Equal(1, attemptCount);
        Assert.Equal(1, maximumActiveAttempts);
        Assert.Equal(0, activeAttempts);
    }

    [Fact]
    public async Task FaultAfterInFlightSignalAndBeforeShutdownIsPremature()
    {
        var mutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedFailure = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observation = TransactionRecoveryFailureObservation.ObserveAsync(
            mutation.Task,
            (_, observedAt) => observedFailure.TrySetResult(observedAt));

        inFlight.TrySetResult();
        await inFlight.Task.WaitAsync(TestContext.Current.CancellationToken);
        mutation.TrySetException(new InvalidOperationException("Pre-shutdown transaction fault"));
        var observedAt = await observedFailure.Task.WaitAsync(TestContext.Current.CancellationToken);
        var shutdownRequestedAt = Stopwatch.GetTimestamp();
        await observation.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(TransactionRecoveryFailureObservation.IsPremature(observedAt, shutdownRequestedAt));
    }
}
