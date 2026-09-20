using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Runtime;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DurableMessagingMetricCardinalityTests : DurableMessagingBehaviorTestBase
{
    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public async Task DistinctValidAndUnknownRoutes_KeepFixedMetricSeriesAndRoutingOutcomes(int routeCount)
    {
        var receiver = NewGrain();
        await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var instruments = context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableMessagingInstruments"));
        var received = (Instrument)instruments.GetType().GetField("_inboxMessagesReceived", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instruments)!;
        using var probe = new MessageMetricListener(received.Meter);
        var grainType = receiver.GetGrainId().Type.ToString();
        var routes = new HashSet<string>();
        for (var index = 0; index < routeCount; index++)
        {
            var unknown = $"unknown/{Guid.NewGuid():N}";
            routes.Add(unknown);
            using var rejected = CreateEnvelope(receiver, NewMessage(index, "unknown"), unknown);
            Assert.Equal(DeliveryStatus.RouteNotFound, (await DeliverAsync(receiver, rejected.Value)).Status);
            Assert.Equal(unknown, rejected.Value.RouteKey);
            var valid = $"messages/cardinality/{Guid.NewGuid():N}";
            routes.Add(valid);
            using var accepted = CreateEnvelope(receiver, NewMessage(index, "accepted"), valid);
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, accepted.Value)).Status);
            Assert.Equal(valid, accepted.Value.RouteKey);
            await Fixture.WaitForEffectCountAsync(receiver, index + 1);
        }

        await probe.WaitForCountAsync("inbox-processing-duration", routeCount);
        var receivedRows = probe.Read("inbox-messages-received");
        Assert.Equal(routeCount * 2, routes.Count);
        Assert.Equal(routeCount * 2, receivedRows.Length);
        Assert.Equal(2, receivedRows.Select(row => row.Series).Distinct().Count());
        var acceptedRows = receivedRows.Where(row => row.Tags.Any(tag => tag.Key == "status" && Equals(tag.Value, "accepted"))).ToArray();
        var rejectedRows = receivedRows.Where(row => row.Tags.Any(tag => tag.Key == "status" && Equals(tag.Value, "route_not_found"))).ToArray();
        Assert.Equal(routeCount, acceptedRows.Length);
        Assert.Equal(routeCount, rejectedRows.Length);
        Assert.All(acceptedRows, row => row.AssertCounter(grainType, "accepted"));
        Assert.All(rejectedRows, row => row.AssertCounter(grainType, "route_not_found"));
        var processed = probe.Read("inbox-messages-processed");
        Assert.Equal(routeCount, processed.Length);
        Assert.Single(processed.Select(row => row.Series).Distinct());
        Assert.All(processed, row => row.AssertCounter(grainType, "success"));
        var durations = probe.Read("inbox-processing-duration");
        Assert.Equal(routeCount, durations.Length);
        Assert.Single(durations.Select(row => row.Series).Distinct());
        Assert.All(durations, row =>
        {
            Assert.Equal("ms", row.Unit);
            Assert.True(row.Value >= 0);
            Assert.Equal(new[] { new KeyValuePair<string, object?>("grain_type", grainType) }, row.Tags);
        });
        var completed = await receiver.GetSnapshotAsync();
        Assert.Equal(routeCount, completed.Effects.Count);
        Assert.All(completed.Effects, effect => Assert.Equal(1, effect.Count));
        Assert.Equal(routeCount, completed.ProcessedMessageCount);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(0, completed.OutboxCount);
        Assert.Empty(completed.InboxDeadLetters);
    }
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DurableMessagingInstrumentTagTests
{
    [Theory]
    [InlineData("OnInboxMessageReceived", "inbox-messages-received", "accepted", false)]
    [InlineData("OnInboxMessageProcessed", "inbox-messages-processed", "success", false)]
    [InlineData("OnOutboxMessageSent", "outbox-messages-sent", null, false)]
    [InlineData("OnOutboxMessageDelivered", "outbox-messages-delivered", "duplicate", false)]
    [InlineData("OnInboxProcessingDuration", "inbox-processing-duration", null, true)]
    [InlineData("OnOutboxDeliveryDuration", "outbox-delivery-duration", null, true)]
    public void MessageInstruments_EmitExactBoundedTagsAndUnchangedValues(string method, string instrumentName, string? status, bool duration)
    {
        using var factory = new TestMeterFactory();
        var instruments = Activator.CreateInstance(ReceiverTestServices.GetImplementationType("DurableMessagingInstruments"),
            new OrleansInstruments(factory))!;
        using var probe = new MessageMetricListener(factory.Meter);
        const string grainType = "instrument-test";
        object[] arguments = duration ? [TimeSpan.FromMilliseconds(12.5), grainType]
            : status is not null ? [grainType, status] : [grainType];
        Invoke(instruments, method, arguments);
        var measurement = Assert.Single(probe.Read(instrumentName));
        Assert.Equal(MessageMetricListener.Prefix + instrumentName, measurement.Name);
        if (duration)
        {
            Assert.Equal("ms", measurement.Unit);
            Assert.Equal(12.5, measurement.Value);
            Assert.Equal(new[] { new KeyValuePair<string, object?>("grain_type", grainType) }, measurement.Tags);
            Invoke(instruments, method, TimeSpan.FromMilliseconds(-10), grainType);
            var values = probe.Read(instrumentName);
            Assert.Equal(2, values.Length);
            Assert.Equal(0, values[1].Value);
            Assert.Equal(measurement.Tags, values[1].Tags);
        }
        else
        {
            measurement.AssertCounter(grainType, status);
        }
    }

    [Fact]
    public void OrphanAndDepthInstruments_RetainExistingTagsAndValues()
    {
        using var factory = new TestMeterFactory();
        var instruments = Activator.CreateInstance(ReceiverTestServices.GetImplementationType("DurableMessagingInstruments"),
            new OrleansInstruments(factory))!;
        using var probe = new MessageMetricListener(factory.Meter);
        Invoke(instruments, "OnOrphanedJobReclaimed", "instrument-test", ReceiverTestServices.InboxJobName);
        Invoke(instruments, "OnInboxDepthChanged", 3);
        Invoke(instruments, "OnInboxDepthChanged", -1);
        Invoke(instruments, "OnOutboxDepthChanged", 4);
        Invoke(instruments, "OnOutboxDepthChanged", -1);
        probe.ObserveGauges();
        var orphan = Assert.Single(probe.Read("orphaned-jobs-reclaimed"));
        Assert.Equal(1, orphan.Value);
        Assert.Null(orphan.Unit);
        Assert.Equal(new[] { new KeyValuePair<string, object?>("grain_type", "instrument-test"), new("job_name", ReceiverTestServices.InboxJobName) }, orphan.Tags);
        var inboxDepth = Assert.Single(probe.Read("inbox-depth"));
        var outboxDepth = Assert.Single(probe.Read("outbox-depth"));
        Assert.Equal(2, inboxDepth.Value);
        Assert.Equal(3, outboxDepth.Value);
        Assert.Empty(inboxDepth.Tags);
        Assert.Empty(outboxDepth.Tags);
        Assert.Null(inboxDepth.Unit);
        Assert.Null(outboxDepth.Unit);
    }

    private static void Invoke(object instruments, string method, params object[] arguments) =>
        instruments.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instruments, arguments);

    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Meter { get; private set; } = null!;
        public Meter Create(MeterOptions options) => Meter = new Meter(options);
        public void Dispose() => Meter.Dispose();
    }
}

internal sealed class MessageMetricListener : IDisposable
{
    internal const string Prefix = "orleans-durable-messaging-";
    private readonly MeterListener _listener;
    private readonly object _lock = new();
    private readonly List<Measurement> _measurements = [];
    private readonly List<(string Name, int Count, TaskCompletionSource Completion)> _waiters = [];

    public MessageMetricListener(Meter meter)
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) && instrument.Name.StartsWith(Prefix, StringComparison.Ordinal))
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        lock (_lock)
        {
            _measurements.Add(new(instrument.Name, instrument.Unit, value, tags.ToArray()));
            foreach (var waiter in _waiters.ToArray())
            {
                if (_measurements.Count(row => row.Name == waiter.Name) >= waiter.Count)
                {
                    _waiters.Remove(waiter);
                    waiter.Completion.TrySetResult();
                }
            }
        }
    }
    public Task WaitForCountAsync(string name, int count)
    {
        lock (_lock)
        {
            if (_measurements.Count(row => row.Name == Prefix + name) >= count) return Task.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((Prefix + name, count, completion));
            return completion.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }
    public Measurement[] Read(string name)
    {
        lock (_lock) return _measurements.Where(row => row.Name == Prefix + name).ToArray();
    }
    public void ObserveGauges() => _listener.RecordObservableInstruments();
    public void Dispose() => _listener.Dispose();
    internal sealed record Measurement(string Name, string? Unit, double Value, KeyValuePair<string, object?>[] Tags)
    {
        public string Series => string.Join(";", Tags.Select(tag => $"{tag.Key}={tag.Value}"));
        public void AssertCounter(string grainType, string? status = null)
        {
            Assert.Equal(1, Value);
            Assert.Null(Unit);
            KeyValuePair<string, object?>[] expected = status is null
                ? [new("grain_type", grainType)]
                : [new("grain_type", grainType), new("status", status)];
            Assert.Equal(expected, Tags);
        }
    }
}
