using System.Diagnostics;
using Orleans.TestingHost.Diagnostics;
using TestExtensions;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("TestingHost")]
public sealed class DiagnosticEventCollectorTests
{
    [Fact]
    public async Task ListenerPrefixes_CaptureMatchingListenerAndExcludeUnrelatedListener()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var listenerPrefix = CreateListenerName("Matching");
        var matchingListenerName = $"{listenerPrefix}.Listener";
        var unrelatedListenerName = CreateListenerName("Unrelated");
        var matchingEventName = $"{matchingListenerName}.Captured";
        var unrelatedEventName = $"{unrelatedListenerName}.Excluded";
        var matchingPayload = new object();
        var unrelatedPayload = new object();
        using var collector = new DiagnosticEventCollector(listenerPrefix);
        using var matchingListener = new DiagnosticListener(matchingListenerName);
        using var unrelatedListener = new DiagnosticListener(unrelatedListenerName);
        var matchingWaiter = collector.WaitForEventAsync(
            matchingEventName,
            Timeout.InfiniteTimeSpan,
            cancellationToken);
        var unrelatedWaiter = collector.CreateEventAwaiter(unrelatedEventName).Task;

        matchingListener.Write(matchingEventName, matchingPayload);
        unrelatedListener.Write(unrelatedEventName, unrelatedPayload);

        var captured = await matchingWaiter;
        Assert.Equal(matchingListenerName, matchingListener.Name);
        Assert.Equal(unrelatedListenerName, unrelatedListener.Name);
        Assert.Equal(matchingEventName, captured.Name);
        Assert.Same(matchingPayload, captured.Payload);
        Assert.NotEqual(default, captured.Timestamp);
        Assert.Equal(captured, Assert.Single(collector.Events));
        Assert.Equal(1, collector.GetEventCount(matchingEventName));
        Assert.Equal(0, collector.GetEventCount(unrelatedEventName));
        await AssertNotCompletedAsync(unrelatedWaiter, cancellationToken);
        await Assert.ThrowsAsync<TimeoutException>(
            () => collector.WaitForEventAsync(unrelatedEventName, TimeSpan.Zero, cancellationToken));
    }

    [Fact]
    public async Task WaitForEventAsync_WhenEventAlreadyExists_ReturnsExactCapturedEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var listenerName = CreateListenerName("Historical");
        var eventName = $"{listenerName}.Existing";
        var payload = new object();
        using var collector = new DiagnosticEventCollector(listenerName);
        using var listener = new DiagnosticListener(listenerName);
        var captureBarrier = collector.CreateEventAwaiter(eventName).Task;

        listener.Write(eventName, payload);

        var captured = await captureBarrier.WaitAsync(cancellationToken);
        var historical = await collector.WaitForEventAsync(
            eventName,
            Timeout.InfiniteTimeSpan,
            cancellationToken);
        Assert.Equal(listenerName, listener.Name);
        Assert.Equal(eventName, historical.Name);
        Assert.Same(payload, historical.Payload);
        Assert.NotEqual(default, historical.Timestamp);
        Assert.Equal(captured, historical);
        Assert.Equal(historical, Assert.Single(collector.Events));
    }

    [Fact]
    public async Task WaitForEventAsync_Predicate_CompletesOnlyMatchingArmedWaiter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var listenerName = CreateListenerName("Predicate");
        var eventName = $"{listenerName}.Value";
        var firstPayload = new object();
        var matchingPayload = new object();
        var neverMatchingPayload = new object();
        using var collector = new DiagnosticEventCollector(listenerName);
        using var listener = new DiagnosticListener(listenerName);
        var matchingWaiter = collector.WaitForEventAsync(
            eventName,
            evt => ReferenceEquals(evt.Payload, matchingPayload),
            Timeout.InfiniteTimeSpan,
            cancellationToken);
        var nonmatchingWaiter = collector.WaitForEventAsync(
            eventName,
            evt => ReferenceEquals(evt.Payload, neverMatchingPayload),
            Timeout.InfiniteTimeSpan,
            cancellationToken);

        listener.Write(eventName, firstPayload);

        await AssertNotCompletedAsync(matchingWaiter, cancellationToken);
        await AssertNotCompletedAsync(nonmatchingWaiter, cancellationToken);

        listener.Write(eventName, matchingPayload);

        var captured = await matchingWaiter;
        Assert.Equal(eventName, captured.Name);
        Assert.Same(matchingPayload, captured.Payload);
        Assert.NotEqual(default, captured.Timestamp);
        await AssertNotCompletedAsync(nonmatchingWaiter, cancellationToken);
        Assert.Equal(2, collector.GetEventCount(eventName));
        Assert.Equal(
            [firstPayload, matchingPayload],
            collector.GetEvents(eventName).Select(static evt => evt.Payload));
    }

    [Fact]
    public async Task CreateEventAwaiter_IgnoresHistoricalEventAndCompletesForNextEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var listenerName = CreateListenerName("Future");
        var eventName = $"{listenerName}.Value";
        var historicalPayload = new object();
        var nextPayload = new object();
        using var collector = new DiagnosticEventCollector(listenerName);
        using var listener = new DiagnosticListener(listenerName);
        var historicalBarrier = collector.CreateEventAwaiter(eventName).Task;

        listener.Write(eventName, historicalPayload);

        var historical = await historicalBarrier.WaitAsync(cancellationToken);
        var futureAwaiter = collector.CreateEventAwaiter(eventName).Task;
        await AssertNotCompletedAsync(futureAwaiter, cancellationToken);

        listener.Write(eventName, nextPayload);

        var next = await futureAwaiter.WaitAsync(cancellationToken);
        Assert.Equal(eventName, next.Name);
        Assert.Same(nextPayload, next.Payload);
        Assert.NotEqual(default, next.Timestamp);
        Assert.NotEqual(historical, next);
        Assert.Same(historicalPayload, historical.Payload);
        Assert.Equal(
            [historicalPayload, nextPayload],
            collector.GetEvents(eventName).Select(static evt => evt.Payload));
        Assert.Equal(2, collector.GetEventCount(eventName));
    }

    [Fact]
    public async Task WaitForEventCountAsync_TimeoutReportsExpectedAndActualCountsExactly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var listenerName = CreateListenerName("Count");
        var eventName = $"{listenerName}.Counted";
        var firstPayload = new object();
        var secondPayload = new object();
        using var collector = new DiagnosticEventCollector(listenerName);
        using var listener = new DiagnosticListener(listenerName);
        var countBarrier = collector.WaitForEventCountAsync(
            eventName,
            2,
            Timeout.InfiniteTimeSpan,
            cancellationToken);

        listener.Write(eventName, firstPayload);
        listener.Write(eventName, secondPayload);

        var captured = await countBarrier;
        Assert.Equal(2, captured.Count);
        Assert.Equal([firstPayload, secondPayload], captured.Select(static evt => evt.Payload));

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => collector.WaitForEventCountAsync(eventName, 3, TimeSpan.Zero, cancellationToken));

        Assert.Equal(
            $"Timed out waiting for 3 '{eventName}' events after 00:00:00. Got 2 events.",
            exception.Message);
        Assert.Contains(listenerName, exception.Message, StringComparison.Ordinal);
        Assert.Contains(eventName, exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, collector.GetEventCount(eventName));
    }

    [Fact]
    public async Task Clear_RemovesCapturedEventsWithoutBreakingSubsequentWaits()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var listenerName = CreateListenerName("Clear");
        var eventName = $"{listenerName}.Value";
        var initialPayload = new object();
        var subsequentPayload = new object();
        using var collector = new DiagnosticEventCollector(listenerName);
        using var listener = new DiagnosticListener(listenerName);
        var initialBarrier = collector.CreateEventAwaiter(eventName).Task;

        listener.Write(eventName, initialPayload);

        var initial = await initialBarrier.WaitAsync(cancellationToken);
        Assert.Equal(eventName, initial.Name);
        Assert.Same(initialPayload, initial.Payload);
        Assert.Equal(initial, Assert.Single(collector.Events));
        Assert.Equal(1, collector.GetEventCount(eventName));

        collector.Clear();

        Assert.Empty(collector.Events);
        Assert.Empty(collector.GetEvents(eventName));
        Assert.Equal(0, collector.GetEventCount(eventName));
        var subsequentWaiter = collector.WaitForEventAsync(
            eventName,
            Timeout.InfiniteTimeSpan,
            cancellationToken);

        listener.Write(eventName, subsequentPayload);

        var subsequent = await subsequentWaiter;
        Assert.Equal(eventName, subsequent.Name);
        Assert.Same(subsequentPayload, subsequent.Payload);
        Assert.NotEqual(initial, subsequent);
        Assert.Equal(subsequent, Assert.Single(collector.Events));
        Assert.Equal(1, collector.GetEventCount(eventName));
    }

    [Fact]
    public async Task Dispose_UnsubscribesAndIsIdempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var listenerName = CreateListenerName("Dispose");
        var capturedEventName = $"{listenerName}.BeforeDispose";
        var excludedEventName = $"{listenerName}.AfterDispose";
        var capturedPayload = new object();
        var excludedPayload = new object();
        var collector = new DiagnosticEventCollector(listenerName);
        using var listener = new DiagnosticListener(listenerName);
        var captureBarrier = collector.CreateEventAwaiter(capturedEventName).Task;
        var excludedWaiter = collector.CreateEventAwaiter(excludedEventName).Task;

        listener.Write(capturedEventName, capturedPayload);

        var captured = await captureBarrier.WaitAsync(cancellationToken);
        var evidenceBeforeDispose = collector.Events;

        var firstDisposeException = Record.Exception(collector.Dispose);
        var secondDisposeException = Record.Exception(collector.Dispose);
        listener.Write(excludedEventName, excludedPayload);

        Assert.Null(firstDisposeException);
        Assert.Null(secondDisposeException);
        Assert.Equal(evidenceBeforeDispose, collector.Events);
        Assert.Equal(captured, Assert.Single(collector.Events));
        Assert.Same(capturedPayload, captured.Payload);
        Assert.Equal(1, collector.GetEventCount(capturedEventName));
        Assert.Equal(0, collector.GetEventCount(excludedEventName));
        await AssertNotCompletedAsync(excludedWaiter, cancellationToken);
        await Assert.ThrowsAsync<TimeoutException>(
            () => collector.WaitForEventAsync(excludedEventName, TimeSpan.Zero, cancellationToken));
    }

    private static string CreateListenerName(string scenario) =>
        $"Orleans.TestingHost.Tests.DiagnosticEventCollector.{scenario}.{Guid.NewGuid():N}";

    private static async Task AssertNotCompletedAsync(Task task, CancellationToken cancellationToken)
    {
        await Assert.ThrowsAsync<TimeoutException>(
            () => task.WaitAsync(TimeSpan.Zero, cancellationToken));
        Assert.False(task.IsCompleted);
    }
}
