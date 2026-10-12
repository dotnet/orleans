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
    private readonly Histogram<double> _inboxProcessingDuration = instruments.Meter.CreateHistogram<double>("orleans-messaging-inbox-processing-duration", MillisecondsUnit);
    private readonly UpDownCounter<long> _inboxDepth = instruments.Meter.CreateUpDownCounter<long>("orleans-messaging-inbox-depth");

    internal static MessagingInstruments CreateForDirectConstruction() => new(new OrleansInstruments(new DirectMeterFactory()));

    internal void OnInboxDepthChanged(string grainType, int delta) => AddDepth(_inboxDepth, grainType, delta);

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
