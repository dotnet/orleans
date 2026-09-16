#nullable enable

using System;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime.Dissemination;
using Xunit;

namespace UnitTests.Dissemination;

// Pump failure metrics have no peer tag, so throwing listeners must not overlap other tests.
[Collection(DisseminationDiagnosticCollection.Name)]
public partial class DisseminationProtocolTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public Task PumpFailureMetricExceptionsPreserveAcceptedWorkAndCapacity(int failedTimerChanges, bool failScheduleMetric) =>
        VerifyPumpFailureMetricIsolation(failedTimerChanges, failScheduleMetric);

    [Fact]
    public Task PumpFailureMetricExceptionsPreservePermanentFailureAndWaiters() =>
        VerifyPumpFailureMetricIsolation(failedTimerChanges: 2, failScheduleMetric: false);

    [Theory]
    [InlineData("warning")]
    [InlineData("pump-failure")]
    [InlineData("retry-schedule")]
    public Task RecoveryDiagnosticLoggerExceptionsFailPumpAndPendingWaiters(string failureStage) =>
        VerifyPumpFailureMetricIsolation(
            failedTimerChanges: 1, failScheduleMetric: failureStage == "retry-schedule", recoveryLogFailureStage: failureStage);

    private static async Task VerifyPumpFailureMetricIsolation(int failedTimerChanges, bool failScheduleMetric, string? recoveryLogFailureStage = null)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(40951);
        var peer = CreateSilo(40952);
        var clock = new RecordingFakeTimeProvider();
        var logger = new PumpDiagnosticsLogger();
        var ns = new FakeNamespace(local);
        ns.Options.MaxPendingItemCount = 1;
        ns.Options.MaxCoalescingDelay = TimeSpan.FromSeconds(1);
        ns.SetValue("original", 7);
        ns.SetValue("next", 9);
        var transport = new FakeTransport(local, peer);
        var started = Enumerable.Range(0, 3)
            .Select(static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var release = Enumerable.Range(0, 3)
            .Select(static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var sent = new ConcurrentQueue<(string Key, long Version, long Payload)>();
        var sendCount = 0;
        transport.SendBroadcastResponseHandler = async (_, batch, token) =>
        {
            var value = Assert.Single(GetBroadcastValues(batch)).Value;
            sent.Enqueue((value.Key.ToString(), value.ToVersion, BitConverter.ToInt64(value.Payload.Span)));
            var attempt = Interlocked.Increment(ref sendCount);
            if (attempt <= started.Length)
            {
                started[attempt - 1].TrySetResult();
                await release[attempt - 1].Task.WaitAsync(token);
            }

            return FakeTransport.CreateAcknowledgment(batch);
        };
        var responseFailure = new InvalidOperationException("The first response observer fails before acknowledgment processing.");
        var responseCount = 0;
        var options = new DisseminationOptions { Enabled = true, MaxConcurrentSends = 1 };
        options.Overlay.AntiEntropyInterval = TimeSpan.FromSeconds(4);
        var queue = new DisseminationBroadcastQueue(
            clock, local, transport.GrainFactory, new TestOptionsMonitor<DisseminationOptions>(options), [ns], logger,
            responseObserver: (_, _) =>
            {
                if (Interlocked.Increment(ref responseCount) == 1)
                {
                    throw responseFailure;
                }
            });
        using var schedules = new BroadcastScheduleObserver();
        using var listener = new MeterListener();
        var pumpMetricFailure = new InvalidOperationException("Pump failure metric callback fails.");
        var scheduleMetricFailure = new InvalidOperationException("Retry schedule metric callback fails.");
        var pumpStatuses = new ConcurrentQueue<string>();
        var scheduleMetricFailures = 0;
        var listenerArmed = 0;
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (Volatile.Read(ref listenerArmed) != 0
                && instrument.Meter.Name == DisseminationInstruments.MeterName
                && (instrument.Name == DisseminationInstruments.PumpFailuresName
                    || failScheduleMetric && instrument.Name == DisseminationInstruments.BroadcastScheduledName))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            if (Volatile.Read(ref listenerArmed) == 0)
            {
                return;
            }

            if (instrument.Name == DisseminationInstruments.PumpFailuresName)
            {
                pumpStatuses.Enqueue((string)Assert.Single(tags.ToArray(), static tag => tag.Key == "status").Value!);
                throw pumpMetricFailure;
            }

            if (tags.ToArray().Any(static tag => tag.Key == "reason" && Equals(tag.Value, "retry")))
            {
                Interlocked.Increment(ref scheduleMetricFailures);
                throw scheduleMetricFailure;
            }
        });
        var recoveryLogFailure = new InvalidOperationException("The recovery diagnostic logger fails.");
        var pendingRecoveryFlush = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveryLogFailures = 0;
        if (recoveryLogFailureStage is not null)
        {
            logger.OnLog = (level, exception) =>
            {
                if (level == LogLevel.Warning)
                {
                    var pendingFlush = queue.FlushPendingBroadcast(cancellationToken);
                    Assert.False(pendingFlush.IsCompleted);
                    pendingRecoveryFlush.TrySetResult(pendingFlush);
                }

                var shouldThrow = recoveryLogFailureStage switch
                {
                    "warning" => level == LogLevel.Warning,
                    "pump-failure" => ReferenceEquals(exception, pumpMetricFailure),
                    "retry-schedule" => ReferenceEquals(exception, scheduleMetricFailure),
                    _ => false,
                };
                if (pendingRecoveryFlush.Task.IsCompletedSuccessfully && shouldThrow
                    && Interlocked.CompareExchange(ref recoveryLogFailures, 1, 0) == 0)
                {
                    throw recoveryLogFailure;
                }
            };
        }

        Task? stop = null;
        try
        {
            var initialSchedule = schedules.WaitAsync(
                value => value.LocalSilo.Equals(local) && value.Peer.Equals(peer)
                    && value.Reason == DisseminationBroadcastScheduleReason.Immediate,
                TimeSpan.FromSeconds(5), cancellationToken);
            Assert.True(queue.Notify(peer, ns, "original"));
            await WaitForPhase(initialSchedule, "initial timer armed");
            var initial = await initialSchedule;
            Assert.Equal(TimeSpan.Zero, initial.DueTime);
            Assert.Equal(0, initial.Attempt);
            clock.Advance(initial.DueTime);
            await WaitForPhase(started[0].Task, "original send in flight");
            Assert.False(queue.Notify(peer, ns, "next"));

            var firstFlush = queue.FlushPendingBroadcast(cancellationToken);
            Assert.False(firstFlush.IsCompleted);
            var retrySchedule = failedTimerChanges < 2 && recoveryLogFailureStage is null
                ? schedules.WaitAsync(
                    value => value.LocalSilo.Equals(local) && value.Peer.Equals(peer)
                        && value.Reason == DisseminationBroadcastScheduleReason.Retry,
                    TimeSpan.FromSeconds(5), cancellationToken)
                : null;
            Volatile.Write(ref listenerArmed, 1);
            listener.Start();
            clock.ThrowOnNextTimerChanges(failedTimerChanges);
            release[0].TrySetResult();

            if (failedTimerChanges == 2 || recoveryLogFailureStage is not null)
            {
                var failedFlush = firstFlush;
                if (recoveryLogFailureStage is not null)
                {
                    await WaitForPhase(firstFlush, "active waiter completed before recovery diagnostics");
                    await WaitForPhase(pendingRecoveryFlush.Task, "pending waiter attached before recovery logger throws");
                    failedFlush = await pendingRecoveryFlush.Task;
                }

                var failure = await Assert.ThrowsAsync<AggregateException>(
                    () => WaitForPhase(failedFlush, "permanently failed flush waiter"));
                var notificationFailure = Assert.Throws<InvalidOperationException>(() => queue.Notify(peer, ns, "next"));
                Assert.Same(failure, notificationFailure.InnerException);
                var laterFlushFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => WaitForPhase(queue.FlushPendingBroadcast(cancellationToken), "later flush observes terminal failure"));
                Assert.Same(failure, laterFlushFailure.InnerException);
                stop = queue.StopAsync(cancellationToken);
                var stopFailure = await Assert.ThrowsAsync<AggregateException>(
                    () => WaitForPhase(stop, "permanently failed drain waiter"));
                Assert.Same(failure, stopFailure);
                Assert.Equal(2, failure.InnerExceptions.Count);
                Assert.Equal("The test timer fails one scheduled change.", failure.InnerExceptions[0].Message);
                if (recoveryLogFailureStage is null)
                {
                    Assert.Equal("The test timer fails one scheduled change.", failure.InnerExceptions[1].Message);
                }
                else
                {
                    Assert.Same(recoveryLogFailure, failure.InnerExceptions[1]);
                    Assert.Equal(1, Volatile.Read(ref recoveryLogFailures));
                }

                Assert.Same(failure, Assert.Single(logger.Entries, static entry => entry.Level == LogLevel.Error).Exception);
                var recoveredMetrics = recoveryLogFailureStage is "pump-failure" or "retry-schedule" ? 2 : 1;
                Assert.Equal(Enumerable.Repeat("recovered", recoveredMetrics).Append("permanent"), pumpStatuses);
                Assert.Equal(1, Volatile.Read(ref sendCount));
            }
            else
            {
                await WaitForPhase(firstFlush, "failed iteration flush waiter released");
                await WaitForPhase(retrySchedule!, "recovered retry timer armed");
                var retry = await retrySchedule!;
                Assert.Equal(failedTimerChanges + 1, retry.Attempt);
                Assert.Equal(TimeSpan.FromSeconds(failedTimerChanges + 1), retry.DueTime);
                Assert.Equal(initial.Epoch, retry.Epoch);
                Assert.Equal(1, Volatile.Read(ref sendCount));

                clock.Advance(retry.DueTime);
                await WaitForPhase(started[1].Task, "original accepted value retried");
                var retryFlush = queue.FlushPendingBroadcast(cancellationToken);
                Assert.False(retryFlush.IsCompleted);
                release[1].TrySetResult();
                await WaitForPhase(retryFlush, "original accepted value acknowledged");
                Assert.Equal(new[] { ("original", 7L, 7L), ("original", 7L, 7L) }, sent);
                Assert.True(queue.Notify(peer, ns, "original", force: false));
                await WaitForPhase(queue.FlushPendingBroadcast(cancellationToken), "acknowledged duplicate suppressed");
                Assert.Equal(2, ns.RepairRequestCount);
                Assert.Equal(2, Volatile.Read(ref sendCount));

                var nextSchedule = schedules.WaitAsync(
                    value => value.LocalSilo.Equals(local) && value.Peer.Equals(peer)
                        && value.Reason == DisseminationBroadcastScheduleReason.Immediate && value.Epoch > retry.Epoch,
                    TimeSpan.FromSeconds(5), cancellationToken);
                Assert.True(queue.Notify(peer, ns, "next"));
                await WaitForPhase(nextSchedule, "freed capacity schedules the next key");
                var next = await nextSchedule;
                Assert.Equal(0, next.Attempt);
                Assert.Equal(retry.Epoch + 1, next.Epoch);
                clock.Advance(next.DueTime);
                await WaitForPhase(started[2].Task, "next key admitted to transport");
                var nextFlush = queue.FlushPendingBroadcast(cancellationToken);
                Assert.False(nextFlush.IsCompleted);
                release[2].TrySetResult();
                await WaitForPhase(nextFlush, "next key acknowledged");
                Assert.True(queue.Notify(peer, ns, "next", force: false));
                await WaitForPhase(queue.FlushPendingBroadcast(cancellationToken), "next acknowledged duplicate suppressed");
                Assert.Equal(new[] { ("original", 7L, 7L), ("original", 7L, 7L), ("next", 9L, 9L) }, sent);
                Assert.Equal(3, ns.RepairRequestCount);
                Assert.Equal(3, Volatile.Read(ref sendCount));
                Assert.Equal(Enumerable.Repeat("recovered", failedTimerChanges + 1), pumpStatuses);
                Assert.Equal(failedTimerChanges, logger.Entries.Count(static entry => entry.Level == LogLevel.Warning));
                Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);
            }

            Assert.Same(responseFailure, Assert.Single(logger.Entries, entry => ReferenceEquals(entry.Exception, responseFailure)).Exception);
            Assert.Equal(pumpStatuses.Count, logger.Entries.Count(entry =>
                entry.Level == LogLevel.Debug && ReferenceEquals(entry.Exception, pumpMetricFailure)));
            Assert.Equal(failScheduleMetric ? 1 : 0, Volatile.Read(ref scheduleMetricFailures));
            Assert.Equal(scheduleMetricFailures, logger.Entries.Count(entry =>
                entry.Level == LogLevel.Debug && ReferenceEquals(entry.Exception, scheduleMetricFailure)));
        }
        finally
        {
            Volatile.Write(ref listenerArmed, 0);
            listener.Dispose();
            clock.ThrowOnNextTimerChanges(0);
            foreach (var completion in release)
            {
                completion.TrySetResult();
            }

            if (stop is null)
            {
                ns.Options.Enabled = false;
                using var cleanup = new CancellationTokenSource();
                cleanup.Cancel();
                try
                {
                    await WaitForPhase(queue.StopAsync(cleanup.Token), "queue cleanup");
                }
                catch (OperationCanceledException exception) when (exception.CancellationToken == cleanup.Token)
                {
                    // An assertion failure can leave accepted work behind a fake-time retry; cancel its drain.
                }
            }
        }

        async Task WaitForPhase(Task task, string phase)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Timed out during {phase} for {local} -> {peer}; sends={Volatile.Read(ref sendCount)}, timer failures={failedTimerChanges}.",
                    exception);
            }
        }
    }

    private sealed class PumpDiagnosticsLogger : ILogger<DisseminationBroadcastQueue>
    {
        public ConcurrentQueue<(LogLevel Level, Exception? Exception)> Entries { get; } = new();

        public Action<LogLevel, Exception?>? OnLog { get; set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue((logLevel, exception));
            OnLog?.Invoke(logLevel, exception);
        }
    }
}
