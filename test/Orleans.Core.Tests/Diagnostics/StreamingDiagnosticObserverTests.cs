using System.Net;
using System.Reactive.Subjects;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using TestExtensions;
using Xunit;
using StreamingEvents = Orleans.Streaming.Diagnostics.StreamingEvents;

namespace UnitTests.Diagnostics;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
[TestCategory("BVT")]
public class StreamingDiagnosticObserverTests
{
    private static readonly SiloAddress Silo = SiloAddress.New(IPAddress.Loopback, 12030, 1);
    private const string Provider = "provider";
    private const string NoEvents = "No matching lifecycle events were observed.";

    [Fact]
    public void Diagnostics_FormatsOnlyLatestSixteenEvents()
    {
        using var events = new Subject<StreamingEvents.StreamingEvent>();
        using var observer = new StreamingDiagnosticObserver(DiagnosticObserverSiloScope.For(Silo), events);
        var streamId = StreamId.Create("diagnostics", Guid.NewGuid());
        var subscriptionId = Guid.NewGuid();
        var failures = Enumerable.Range(0, 65).Select(index => new CountingException($"failure-{index}")).ToArray();
        for (var index = 0; index < 64; index++)
        {
            events.OnNext(Failure(streamId, subscriptionId, failures[index], index));
        }

        Assert.All(failures, failure => Assert.Equal(0, failure.FormatCount));
        var first = observer.GetSubscriptionDiagnostics(streamId, subscriptionId, Provider);
        var lines = first.Split(Environment.NewLine);
        Assert.Equal(16, lines.Length);
        for (var index = 0; index < 16; index++)
        {
            Assert.EndsWith($": failure-{index + 48}", lines[index]);
        }

        Assert.All(failures.Take(48), failure => Assert.Equal(0, failure.FormatCount));
        Assert.All(failures.Skip(48).Take(16), failure => Assert.Equal(1, failure.FormatCount));
        Assert.Equal(first, observer.GetSubscriptionDiagnostics(streamId, subscriptionId, Provider));
        Assert.All(failures.Skip(48).Take(16), failure => Assert.Equal(2, failure.FormatCount));

        events.OnNext(Failure(streamId, subscriptionId, failures[64], 64));
        var updated = observer.GetSubscriptionDiagnostics(streamId, subscriptionId, Provider).Split(Environment.NewLine);
        Assert.Equal(16, updated.Length);
        Assert.EndsWith(": failure-49", updated[0]);
        Assert.EndsWith(": failure-64", updated[^1]);
        Assert.Equal(2, failures[48].FormatCount);
        Assert.Equal(1, failures[64].FormatCount);
    }

    [Fact]
    public void Diagnostics_IsolatesProviderStreamSubscriptionAndSilo()
    {
        using var events = new Subject<StreamingEvents.StreamingEvent>();
        using var observer = new StreamingDiagnosticObserver(DiagnosticObserverSiloScope.For(Silo), events);
        var streamId = StreamId.Create("diagnostics", Guid.NewGuid());
        var otherStream = StreamId.Create("diagnostics", Guid.NewGuid());
        var subscriptionId = Guid.NewGuid();
        var otherSubscription = Guid.NewGuid();
        var consumer = new Consumer();
        events.OnNext(new StreamingEvents.SubscriptionUnregistration(
            Provider, streamId, subscriptionId, Silo, consumer,
            StreamingEvents.SubscriptionUnregistrationStage.Requested, null));
        for (var index = 0; index < 20; index++)
        {
            events.OnNext(new StreamingEvents.SubscriptionUnregistration(
                "other-provider", streamId, subscriptionId, Silo, consumer,
                StreamingEvents.SubscriptionUnregistrationStage.Completed, null));
            events.OnNext(new StreamingEvents.SubscriptionUnregistration(
                Provider, otherStream, subscriptionId, Silo, consumer,
                StreamingEvents.SubscriptionUnregistrationStage.Completed, null));
            events.OnNext(new StreamingEvents.SubscriptionUnregistration(
                Provider, streamId, otherSubscription, Silo, consumer,
                StreamingEvents.SubscriptionUnregistrationStage.Completed, null));
            events.OnNext(new StreamingEvents.SubscriptionUnregistration(
                Provider, streamId, subscriptionId, SiloAddress.New(IPAddress.Loopback, 12031, 1), consumer,
                StreamingEvents.SubscriptionUnregistrationStage.Failed, new InvalidOperationException("other silo")));
        }

        Assert.Equal($"Unregistration Requested on {Silo} for consumer: ",
            observer.GetSubscriptionDiagnostics(streamId, subscriptionId, Provider));
        Assert.Equal(16, observer.GetSubscriptionDiagnostics(streamId, subscriptionId, "other-provider").Split(Environment.NewLine).Length);
        Assert.Equal(16, observer.GetSubscriptionDiagnostics(otherStream, subscriptionId, Provider).Split(Environment.NewLine).Length);
        Assert.Equal(16, observer.GetSubscriptionDiagnostics(streamId, otherSubscription, Provider).Split(Environment.NewLine).Length);
        Assert.Equal(NoEvents, observer.GetSubscriptionDiagnostics(otherStream, otherSubscription, Provider));
    }

    [Fact]
    public async Task Diagnostics_EvictionPreservesHistoricalAndFutureLifecycleWaits()
    {
        using var events = new Subject<StreamingEvents.StreamingEvent>();
        using var observer = new StreamingDiagnosticObserver(DiagnosticObserverSiloScope.For(Silo), events);
        var streamId = StreamId.Create("diagnostics", Guid.NewGuid());
        var subscriptionId = Guid.NewGuid();
        var consumerId = GrainId.Create("consumer", "one");
        var unregistered = new StreamingEvents.SubscriptionUnregistered(Provider, streamId, subscriptionId, Silo);
        events.OnNext(new StreamingEvents.SubscriptionAttached(Provider, streamId, subscriptionId, consumerId, Silo));
        events.OnNext(new StreamingEvents.ConsumerCursorDrained(Provider, streamId, subscriptionId, Silo));
        events.OnNext(unregistered);
        Assert.Equal(
            new[] { $"Attached on {Silo} to {consumerId}", $"Cursor drained on {Silo}", $"Durably unregistered on {Silo}" },
            observer.GetSubscriptionDiagnostics(streamId, subscriptionId, Provider).Split(Environment.NewLine));

        for (var index = 0; index < 16; index++)
        {
            events.OnNext(new StreamingEvents.ConsumerCursorDrained(Provider, streamId, subscriptionId, Silo));
        }

        var diagnostics = observer.GetSubscriptionDiagnostics(streamId, subscriptionId, Provider).Split(Environment.NewLine);
        Assert.Equal(16, diagnostics.Length);
        Assert.All(diagnostics, entry => Assert.Equal($"Cursor drained on {Silo}", entry));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        Assert.Same(unregistered, await observer.WaitForSubscriptionUnregisteredAsync(streamId, subscriptionId, Provider, cancellation.Token));
        var nextSubscription = Guid.NewGuid();
        var future = observer.WaitForSubscriptionUnregisteredAsync(streamId, nextSubscription, Provider, cancellation.Token);
        Assert.False(future.IsCompleted);
        var next = new StreamingEvents.SubscriptionUnregistered(Provider, streamId, nextSubscription, Silo);
        events.OnNext(next);
        Assert.Same(next, await future);
    }

    [Fact]
    public async Task Diagnostics_FormattingDoesNotBlockNewEvents()
    {
        using var events = new Subject<StreamingEvents.StreamingEvent>();
        using var observer = new StreamingDiagnosticObserver(DiagnosticObserverSiloScope.For(Silo), events);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        var streamId = StreamId.Create("diagnostics", Guid.NewGuid());
        var subscriptionId = Guid.NewGuid();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new BlockingException(entered, release, cancellation.Token);
        events.OnNext(Failure(streamId, subscriptionId, failure));
        var snapshot = Task.Run(() => observer.GetSubscriptionDiagnostics(streamId, subscriptionId, Provider), cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(cancellation.Token);
            await Task.Run(() => events.OnNext(
                new StreamingEvents.ConsumerCursorDrained(Provider, streamId, subscriptionId, Silo)), cancellation.Token)
                .WaitAsync(cancellation.Token);
        }
        finally
        {
            release.Set();
        }

        Assert.Single((await snapshot.WaitAsync(cancellation.Token)).Split(Environment.NewLine));
        var current = observer.GetSubscriptionDiagnostics(streamId, subscriptionId, Provider).Split(Environment.NewLine);
        Assert.Equal(2, current.Length);
        Assert.EndsWith(": blocked-format", current[0]);
        Assert.Equal($"Cursor drained on {Silo}", current[1]);
    }

    [Fact]
    public void Dispose_ReleasesSubscriptionAndDiagnosticHistory()
    {
        using var events = new Subject<StreamingEvents.StreamingEvent>();
        var observer = new StreamingDiagnosticObserver(DiagnosticObserverSiloScope.For(Silo), events);
        var streamId = StreamId.Create("diagnostics", Guid.NewGuid());
        var subscriptionId = Guid.NewGuid();
        events.OnNext(new StreamingEvents.ConsumerCursorDrained(Provider, streamId, subscriptionId, Silo));
        Assert.True(events.HasObservers);
        Assert.Equal($"Cursor drained on {Silo}", observer.GetSubscriptionDiagnostics(streamId, subscriptionId, Provider));

        observer.Dispose();
        observer.Dispose();
        events.OnNext(new StreamingEvents.ConsumerCursorDrained(Provider, streamId, subscriptionId, Silo));

        Assert.False(events.HasObservers);
        Assert.Equal(NoEvents, observer.GetSubscriptionDiagnostics(streamId, subscriptionId, Provider));
    }

    private static StreamingEvents.MessageDeliveryFailed Failure(
        StreamId streamId, Guid subscriptionId, Exception exception, int sequence = 0)
        => new(Provider, streamId, subscriptionId, Silo, new Consumer(), new EventSequenceTokenV2(sequence), exception);

    private sealed class Consumer : IAddressable
    {
        public override string ToString() => "consumer";
    }

    private sealed class CountingException(string message) : Exception(message)
    {
        public int FormatCount { get; private set; }

        public override string ToString()
        {
            FormatCount++;
            return Message;
        }
    }

    private sealed class BlockingException(
        TaskCompletionSource entered,
        ManualResetEventSlim release,
        CancellationToken cancellationToken) : Exception
    {
        public override string ToString()
        {
            entered.TrySetResult();
            release.Wait(cancellationToken);
            return "blocked-format";
        }
    }
}
