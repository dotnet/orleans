using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using Orleans.Runtime;

namespace Orleans.DurableMessaging;

internal sealed class DurableMessagingInstruments(OrleansInstruments instruments)
{
    private const string MillisecondsUnit = "ms";
    private const string GrainTypeTagName = "grain_type";
    private const string RouteKeyTagName = "route_key";
    private const string StatusTagName = "status";

    private readonly Counter<long> _inboxMessagesReceived = instruments.Meter.CreateCounter<long>("orleans-durable-messaging-inbox-messages-received");
    private readonly Counter<long> _inboxMessagesProcessed = instruments.Meter.CreateCounter<long>("orleans-durable-messaging-inbox-messages-processed");
    private readonly Counter<long> _outboxMessagesSent = instruments.Meter.CreateCounter<long>("orleans-durable-messaging-outbox-messages-sent");
    private readonly Counter<long> _outboxMessagesDelivered = instruments.Meter.CreateCounter<long>("orleans-durable-messaging-outbox-messages-delivered");
    private readonly Counter<long> _orphanedJobsReclaimed = instruments.Meter.CreateCounter<long>("orleans-durable-messaging-orphaned-jobs-reclaimed");
    private readonly Histogram<double> _inboxProcessingDuration = instruments.Meter.CreateHistogram<double>("orleans-durable-messaging-inbox-processing-duration", MillisecondsUnit);
    private readonly Histogram<double> _outboxDeliveryDuration = instruments.Meter.CreateHistogram<double>("orleans-durable-messaging-outbox-delivery-duration", MillisecondsUnit);
    private readonly DepthTracker _inboxDepth = new(instruments.Meter, "orleans-durable-messaging-inbox-depth");
    private readonly DepthTracker _outboxDepth = new(instruments.Meter, "orleans-durable-messaging-outbox-depth");

    internal static DurableMessagingInstruments CreateForDirectConstruction() => new(new OrleansInstruments(new DirectMeterFactory()));

    internal void OnInboxDepthChanged(int delta) => _inboxDepth.Adjust(delta);

    internal void OnOutboxDepthChanged(int delta) => _outboxDepth.Adjust(delta);

    internal void OnInboxMessageReceived(string grainType, string routeKey, string status) =>
        Add(_inboxMessagesReceived, grainType, routeKey, status);

    internal void OnInboxMessageProcessed(string grainType, string routeKey, string status) =>
        Add(_inboxMessagesProcessed, grainType, routeKey, status);

    internal void OnInboxProcessingDuration(TimeSpan duration, string grainType, string routeKey) =>
        Record(_inboxProcessingDuration, duration, grainType, routeKey);

    internal void OnOutboxMessageSent(string grainType, string routeKey)
    {
        if (_outboxMessagesSent.Enabled)
        {
            _outboxMessagesSent.Add(1, CreateTags(grainType, routeKey));
        }
    }

    internal void OnOutboxMessageDelivered(string grainType, string routeKey, string status) =>
        Add(_outboxMessagesDelivered, grainType, routeKey, status);

    internal void OnOutboxDeliveryDuration(TimeSpan duration, string grainType, string routeKey) =>
        Record(_outboxDeliveryDuration, duration, grainType, routeKey);

    internal void OnOrphanedJobReclaimed(string grainType, string jobName)
    {
        if (_orphanedJobsReclaimed.Enabled)
        {
            _orphanedJobsReclaimed.Add(
                1,
                [
                    new(GrainTypeTagName, grainType),
                    new("job_name", jobName)
                ]);
        }
    }

    private static void Add(Counter<long> counter, string grainType, string routeKey, string status)
    {
        if (counter.Enabled)
        {
            counter.Add(
                1,
                [
                    new(GrainTypeTagName, grainType),
                    new(RouteKeyTagName, routeKey),
                    new(StatusTagName, status)
                ]);
        }
    }

    private static void Record(Histogram<double> histogram, TimeSpan duration, string grainType, string routeKey)
    {
        if (histogram.Enabled)
        {
            histogram.Record(Math.Max(0, duration.TotalMilliseconds), CreateTags(grainType, routeKey));
        }
    }

    private static KeyValuePair<string, object?>[] CreateTags(string grainType, string routeKey) =>
        [
            new(GrainTypeTagName, grainType),
            new(RouteKeyTagName, routeKey)
        ];

    private sealed class DirectMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);

        public void Dispose()
        {
        }
    }

    private sealed class DepthTracker
    {
        private readonly ObservableGauge<long> _gauge;
        private long _value;

        public DepthTracker(Meter meter, string name)
        {
            _gauge = meter.CreateObservableGauge(name, () => Volatile.Read(ref _value));
        }

        public void Adjust(int delta) => Interlocked.Add(ref _value, delta);
    }
}
