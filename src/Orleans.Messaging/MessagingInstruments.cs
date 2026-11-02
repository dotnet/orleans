using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Orleans.Runtime;

namespace Orleans.Messaging;

internal sealed class MessagingInstruments(OrleansInstruments instruments)
{
    private const string MillisecondsUnit = "ms";
    private const string GrainTypeTagName = "grain_type";
    private const string StatusTagName = "status";

    private readonly Counter<long> _inboxMessagesReceived = instruments.Meter.CreateCounter<long>("orleans-messaging-inbox-messages-received");
    private readonly Counter<long> _inboxMessagesProcessed = instruments.Meter.CreateCounter<long>("orleans-messaging-inbox-messages-processed");
    private readonly Counter<long> _outboxMessagesSent = instruments.Meter.CreateCounter<long>("orleans-messaging-outbox-messages-sent");
    private readonly Counter<long> _outboxMessagesDelivered = instruments.Meter.CreateCounter<long>("orleans-messaging-outbox-messages-delivered");
    private readonly Histogram<double> _inboxProcessingDuration = instruments.Meter.CreateHistogram<double>("orleans-messaging-inbox-processing-duration", MillisecondsUnit);
    private readonly Histogram<double> _outboxDeliveryDuration = instruments.Meter.CreateHistogram<double>("orleans-messaging-outbox-delivery-duration", MillisecondsUnit);
    private readonly UpDownCounter<long> _inboxDepth = instruments.Meter.CreateUpDownCounter<long>("orleans-messaging-inbox-depth");
    private readonly UpDownCounter<long> _outboxDepth = instruments.Meter.CreateUpDownCounter<long>("orleans-messaging-outbox-depth");

    internal static MessagingInstruments CreateForDirectConstruction() => new(new OrleansInstruments(new DirectMeterFactory()));

    internal void OnInboxDepthChanged(string grainType, int delta) => AddDepth(_inboxDepth, grainType, delta);

    internal void OnOutboxDepthChanged(string grainType, int delta) => AddDepth(_outboxDepth, grainType, delta);

    private static void AddDepth(UpDownCounter<long> counter, string grainType, int delta)
    {
        if (delta != 0 && counter.Enabled)
        {
            counter.Add(delta, new KeyValuePair<string, object?>(GrainTypeTagName, grainType));
        }
    }

    internal void OnInboxMessageReceived(string grainType, string status) =>
        Add(_inboxMessagesReceived, grainType, status);

    internal void OnInboxMessageProcessed(string grainType, string status) =>
        Add(_inboxMessagesProcessed, grainType, status);

    internal void OnInboxProcessingDuration(TimeSpan duration, string grainType) =>
        Record(_inboxProcessingDuration, duration, grainType);

    internal void OnOutboxMessageSent(string grainType)
    {
        if (_outboxMessagesSent.Enabled)
        {
            _outboxMessagesSent.Add(1, new KeyValuePair<string, object?>(GrainTypeTagName, grainType));
        }
    }

    internal void OnOutboxMessageDelivered(string grainType, string status) =>
        Add(_outboxMessagesDelivered, grainType, status);

    internal void OnOutboxDeliveryDuration(TimeSpan duration, string grainType) =>
        Record(_outboxDeliveryDuration, duration, grainType);

    private static void Add(Counter<long> counter, string grainType, string status)
    {
        if (counter.Enabled)
        {
            counter.Add(1, new(GrainTypeTagName, grainType), new(StatusTagName, status));
        }
    }

    private static void Record(Histogram<double> histogram, TimeSpan duration, string grainType)
    {
        if (histogram.Enabled)
        {
            histogram.Record(Math.Max(0, duration.TotalMilliseconds), new KeyValuePair<string, object?>(GrainTypeTagName, grainType));
        }
    }

    private sealed class DirectMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);

        public void Dispose()
        {
        }
    }
}
