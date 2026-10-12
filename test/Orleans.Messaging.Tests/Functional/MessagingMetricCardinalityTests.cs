using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class MessagingMetricCardinalityTests : MessagingBehaviorTestBase
{
    [Fact]
    public async Task PendingAndProcessedDuplicates_RecordOneBoundedReceiptEach()
    {
        var receiver = NewGrain();
        await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var services = context.ActivationServices;
        IInboxExtension extension = services.GetRequiredService<Inbox>();
        // The pump runs alongside grain calls, so holding the handler keeps the accepted message pending.
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/metric-pending");
        var envelope = CreateEnvelope(receiver, NewMessage(451, "duplicate-receipts"), "messages/metric-pending");
        Assert.Equal(DeliveryStatus.Accepted, (await StartDelivery()).Status);
        await handler.WaitUntilEnteredAsync();
        Assert.NotEmpty(Fixture.JobManagerProbe.GetScheduledJobs(MessagingPump.JobName, receiver.GetGrainId()));
        var pending = Fixture.GetSnapshot(receiver);
        Assert.Equal(1, pending.InboxCount);
        Assert.Equal(0, pending.ProcessedMessageCount);
        Assert.Empty(pending.Effects);
        using var probe = new MessageMetricListener(services.GetRequiredService<OrleansInstruments>().Meter);
        var grainType = receiver.GetGrainId().Type.ToString();

        Assert.Equal(DeliveryStatus.Duplicate, (await StartDelivery()).Status);
        Assert.Single(probe.Read("inbox-messages-received")).AssertCounter(grainType, "duplicate");
        handler.Release();
        var processed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(0, processed.InboxCount);
        Assert.Equal(1, processed.ProcessedMessageCount);
        Assert.Equal(1, Assert.Single(processed.Effects).Count);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope)).Status);
        var receipts = probe.Read("inbox-messages-received");
        Assert.Equal(2, receipts.Length);
        Assert.All(receipts, receipt => receipt.AssertCounter(grainType, "duplicate"));
        Assert.Single(receipts.Select(receipt => receipt.Series).Distinct());

        Task<DeliveryResult> StartDelivery()
        {
            var started = new TaskCompletionSource<Task<DeliveryResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Scheduler.QueueAction(() =>
            {
                try { started.SetResult(extension.DeliverAsync(envelope, TestContext.Current.CancellationToken).AsTask()); }
                catch (Exception exception) { started.SetException(exception); }
            });
            return started.Task.Unwrap();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public async Task DistinctApplicationRoutes_KeepFixedMetricSeriesAndMissingHandlerOutcomes(int routeCount)
    {
        var receiver = NewGrain();
        var missingHandler = NewGrain();
        await missingHandler.ConfigureHandlerAsync(false);
        await ReactivateAsync(missingHandler);
        await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        using var probe = new MessageMetricListener(context.ActivationServices.GetRequiredService<OrleansInstruments>().Meter);
        var grainType = receiver.GetGrainId().Type.ToString();
        var routes = new HashSet<string>();
        for (var index = 0; index < routeCount; index++)
        {
            var unknown = $"unknown/{Guid.NewGuid():N}";
            routes.Add(unknown);
            var rejected = CreateEnvelope(missingHandler, NewMessage(index, "unknown"), unknown);
            Assert.Equal(DeliveryStatus.HandlerNotFound, (await DeliverAsync(missingHandler, rejected)).Status);
            Assert.Equal(unknown, TestApplicationProtocol.Read(Fixture.Client.ServiceProvider.GetRequiredService<Orleans.Serialization.Session.SerializerSessionPool>(), rejected).Route);
            var valid = $"messages/cardinality/{Guid.NewGuid():N}";
            routes.Add(valid);
            var accepted = CreateEnvelope(receiver, NewMessage(index, "accepted"), valid);
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, accepted)).Status);
            Assert.Equal(valid, TestApplicationProtocol.Read(Fixture.Client.ServiceProvider.GetRequiredService<Orleans.Serialization.Session.SerializerSessionPool>(), accepted).Route);
            await Fixture.WaitForEffectCountAsync(receiver, index + 1);
        }

        await probe.WaitForCountAsync("inbox-processing-duration", routeCount);
        var receivedRows = probe.Read("inbox-messages-received");
        Assert.Equal(routeCount * 2, routes.Count);
        Assert.Equal(routeCount * 2, receivedRows.Length);
        Assert.Equal(2, receivedRows.Select(row => row.Series).Distinct().Count());
        var acceptedRows = receivedRows.Where(row => row.Tags.Any(tag => tag.Key == "status" && Equals(tag.Value, "accepted"))).ToArray();
        var rejectedRows = receivedRows.Where(row => row.Tags.Any(tag => tag.Key == "status" && Equals(tag.Value, "handler_not_found"))).ToArray();
        Assert.Equal(routeCount, acceptedRows.Length);
        Assert.Equal(routeCount, rejectedRows.Length);
        Assert.All(acceptedRows, row => row.AssertCounter(grainType, "accepted"));
        Assert.All(rejectedRows, row => row.AssertCounter(grainType, "handler_not_found"));
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
[TestArea("Messaging")]
public sealed class MessagingInstrumentTagTests
{
    [Theory]
    [InlineData("inbox-messages-received", "accepted")]
    [InlineData("inbox-messages-processed", "success")]
    public void Counters_EmitExactBoundedTags(string instrumentName, string? status)
    {
        using var factory = new TestMeterFactory();
        var instruments = new Orleans.Messaging.MessagingInstruments(new OrleansInstruments(factory));
        using var probe = new MessageMetricListener(factory.Meter);
        const string grainType = "instrument-test";
        Action emit = instrumentName switch
        {
            "inbox-messages-received" => () => instruments.OnInboxMessageReceived(grainType, status!),
            _ => () => instruments.OnInboxMessageProcessed(grainType, status!),
        };
        emit();
        var measurement = Assert.Single(probe.Read(instrumentName));
        Assert.Equal(MessageMetricListener.Prefix + instrumentName, measurement.Name);
        measurement.AssertCounter(grainType, status);
    }

    [Theory]
    [InlineData("inbox-processing-duration")]
    public void Durations_RecordMillisecondsAndClampNegativeValues(string instrumentName)
    {
        using var factory = new TestMeterFactory();
        var instruments = new Orleans.Messaging.MessagingInstruments(new OrleansInstruments(factory));
        using var probe = new MessageMetricListener(factory.Meter);
        const string grainType = "instrument-test";
        Action<TimeSpan> record = duration => instruments.OnInboxProcessingDuration(duration, grainType);
        record(TimeSpan.FromMilliseconds(12.5));
        record(TimeSpan.FromMilliseconds(-10));
        var values = probe.Read(instrumentName);
        Assert.Equal(2, values.Length);
        Assert.All(values, value =>
        {
            Assert.Equal("ms", value.Unit);
            Assert.Equal(new[] { new KeyValuePair<string, object?>("grain_type", grainType) }, value.Tags);
        });
        Assert.Equal(12.5, values[0].Value);
        Assert.Equal(0, values[1].Value);
    }

    [Fact]
    public void DepthInstruments_RecordNonZeroDeltasTaggedByGrainType()
    {
        using var factory = new TestMeterFactory();
        var instruments = new Orleans.Messaging.MessagingInstruments(new OrleansInstruments(factory));
        using var probe = new MessageMetricListener(factory.Meter);
        instruments.OnInboxDepthChanged("instrument-test", 3);
        instruments.OnInboxDepthChanged("instrument-test", 0);
        instruments.OnInboxDepthChanged("instrument-test", -1);
        var inboxDepth = probe.Read("inbox-depth");
        Assert.Equal([3d, -1d], inboxDepth.Select(static row => row.Value));
        Assert.All(inboxDepth, row =>
        {
            Assert.Null(row.Unit);
            Assert.Equal(new[] { new KeyValuePair<string, object?>("grain_type", "instrument-test") }, row.Tags);
        });
    }
    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Meter { get; private set; } = null!;
        public Meter Create(MeterOptions options) => Meter = new Meter(options);
        public void Dispose() => Meter.Dispose();
    }
}

internal sealed class MessageMetricListener : IDisposable
{
    internal const string Prefix = "orleans-messaging-";
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
