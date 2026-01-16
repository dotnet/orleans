using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

/// <summary>
/// Metrics instrumentation for Orleans Journaling durable inbox/outbox messaging.
/// </summary>
public static class JournalingInstruments
{
    // Inbox metrics
    private static readonly Counter<long> InboxMessagesReceivedCounter = Instruments.Meter.CreateCounter<long>(InstrumentNames.JOURNALING_INBOX_MESSAGES_RECEIVED);
    private static readonly Counter<long> InboxMessagesProcessedCounter = Instruments.Meter.CreateCounter<long>(InstrumentNames.JOURNALING_INBOX_MESSAGES_PROCESSED);
    private static readonly Histogram<double> InboxProcessingDurationHistogram = Instruments.Meter.CreateHistogram<double>(InstrumentNames.JOURNALING_INBOX_PROCESSING_DURATION, "ms");

    // Outbox metrics
    private static readonly Counter<long> OutboxMessagesSentCounter = Instruments.Meter.CreateCounter<long>(InstrumentNames.JOURNALING_OUTBOX_MESSAGES_SENT);
    private static readonly Counter<long> OutboxMessagesDeliveredCounter = Instruments.Meter.CreateCounter<long>(InstrumentNames.JOURNALING_OUTBOX_MESSAGES_DELIVERED);
    private static readonly Histogram<double> OutboxDeliveryDurationHistogram = Instruments.Meter.CreateHistogram<double>(InstrumentNames.JOURNALING_OUTBOX_DELIVERY_DURATION, "ms");

    // Observable gauges (registered separately to allow dynamic observation)
    public static ObservableGauge<int> InboxDepthGauge;
    public static ObservableGauge<int> OutboxDepthGauge;

    /// <summary>
    /// Registers an observable gauge for inbox depth with the provided observation function.
    /// </summary>
    /// <param name="observeValue">Function that returns the current inbox depth.</param>
    public static void RegisterInboxDepthObserve(Func<int> observeValue)
    {
        InboxDepthGauge = Instruments.Meter.CreateObservableGauge(
            InstrumentNames.JOURNALING_INBOX_MESSAGES_DEPTH,
            observeValue);
    }

    /// <summary>
    /// Registers an observable gauge for outbox depth with the provided observation function.
    /// </summary>
    /// <param name="observeValue">Function that returns the current outbox depth.</param>
    public static void RegisterOutboxDepthObserve(Func<int> observeValue)
    {
        OutboxDepthGauge = Instruments.Meter.CreateObservableGauge(
            InstrumentNames.JOURNALING_OUTBOX_MESSAGES_DEPTH,
            observeValue);
    }

    /// <summary>
    /// Records a message received by the inbox.
    /// </summary>
    /// <param name="grainType">The grain type that received the message.</param>
    /// <param name="routeKey">The route key for the message handler.</param>
    /// <param name="status">The delivery status (e.g., "accepted", "duplicate", "backpressured", "route_not_found").</param>
    public static void OnInboxMessageReceived(string grainType, string routeKey, string status)
    {
        if (InboxMessagesReceivedCounter.Enabled)
        {
            InboxMessagesReceivedCounter.Add(1,
                [
                    new KeyValuePair<string, object>("grain_type", grainType),
                    new KeyValuePair<string, object>("route_key", routeKey),
                    new KeyValuePair<string, object>("status", status)
                ]);
        }
    }

    /// <summary>
    /// Records a message processed by the inbox (handler invocation completed).
    /// </summary>
    /// <param name="grainType">The grain type that processed the message.</param>
    /// <param name="routeKey">The route key for the message handler.</param>
    /// <param name="status">The processing status (e.g., "success", "error").</param>
    public static void OnInboxMessageProcessed(string grainType, string routeKey, string status)
    {
        if (InboxMessagesProcessedCounter.Enabled)
        {
            InboxMessagesProcessedCounter.Add(1,
                [
                    new KeyValuePair<string, object>("grain_type", grainType),
                    new KeyValuePair<string, object>("route_key", routeKey),
                    new KeyValuePair<string, object>("status", status)
                ]);
        }
    }

    /// <summary>
    /// Records the duration of inbox message processing (handler invocation).
    /// </summary>
    /// <param name="duration">The processing duration.</param>
    /// <param name="grainType">The grain type that processed the message.</param>
    /// <param name="routeKey">The route key for the message handler.</param>
    public static void OnInboxProcessingDuration(TimeSpan duration, string grainType, string routeKey)
    {
        if (InboxProcessingDurationHistogram.Enabled)
        {
            InboxProcessingDurationHistogram.Record(
                duration.TotalMilliseconds,
                [
                    new KeyValuePair<string, object>("grain_type", grainType),
                    new KeyValuePair<string, object>("route_key", routeKey)
                ]);
        }
    }

    /// <summary>
    /// Records a message sent via the outbox.
    /// </summary>
    /// <param name="grainType">The grain type that sent the message.</param>
    /// <param name="routeKey">The route key for the target handler.</param>
    public static void OnOutboxMessageSent(string grainType, string routeKey)
    {
        if (OutboxMessagesSentCounter.Enabled)
        {
            OutboxMessagesSentCounter.Add(1,
                [
                    new KeyValuePair<string, object>("grain_type", grainType),
                    new KeyValuePair<string, object>("route_key", routeKey)
                ]);
        }
    }

    /// <summary>
    /// Records a message delivered via the outbox.
    /// </summary>
    /// <param name="grainType">The grain type that sent the message.</param>
    /// <param name="routeKey">The route key for the target handler.</param>
    /// <param name="status">The delivery status (e.g., "accepted", "duplicate", "backpressured", "route_not_found").</param>
    public static void OnOutboxMessageDelivered(string grainType, string routeKey, string status)
    {
        if (OutboxMessagesDeliveredCounter.Enabled)
        {
            OutboxMessagesDeliveredCounter.Add(1,
                [
                    new KeyValuePair<string, object>("grain_type", grainType),
                    new KeyValuePair<string, object>("route_key", routeKey),
                    new KeyValuePair<string, object>("status", status)
                ]);
        }
    }

    /// <summary>
    /// Records the duration of outbox message delivery attempt.
    /// </summary>
    /// <param name="duration">The delivery duration.</param>
    /// <param name="grainType">The grain type that sent the message.</param>
    /// <param name="routeKey">The route key for the target handler.</param>
    public static void OnOutboxDeliveryDuration(TimeSpan duration, string grainType, string routeKey)
    {
        if (OutboxDeliveryDurationHistogram.Enabled)
        {
            OutboxDeliveryDurationHistogram.Record(
                duration.TotalMilliseconds,
                [
                    new KeyValuePair<string, object>("grain_type", grainType),
                    new KeyValuePair<string, object>("route_key", routeKey)
                ]);
        }
    }
}
