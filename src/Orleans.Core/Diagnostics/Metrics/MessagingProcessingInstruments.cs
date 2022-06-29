using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal static class MessagingProcessingInstruments
{
    private static readonly CounterAggregatorGroup DispatcherMessagesProcessedCounterAggregatorGroup = new();
    private static readonly ObservableCounter<long> DispatcherMessagesProcessedCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.MESSAGING_DISPATCHER_PROCESSED, DispatcherMessagesProcessedCounterAggregatorGroup.Collect);

    private static readonly CounterAggregatorGroup DispatcherMessagesReceivedCounterAggregatorGroup = new();
    private static readonly ObservableCounter<long> DispatcherMessagesReceivedCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.MESSAGING_DISPATCHER_RECEIVED, DispatcherMessagesReceivedCounterAggregatorGroup.Collect);

    private static readonly Counter<long> dispatcherMessagesForwardedCounter = Instruments.Meter.CreateCounter<long>(InstrumentNames.MESSAGING_DISPATCHER_FORWARDED);
    private static readonly CounterAggregator imaReceivedCounterAggregator = new();
    private static readonly ObservableCounter<long> imaReceivedCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.MESSAGING_IMA_RECEIVED, imaReceivedCounterAggregator.Collect);
    private static readonly Counter<long> imaEnqueuedCounter = Instruments.Meter.CreateCounter<long>(InstrumentNames.MESSAGING_IMA_ENQUEUED);

    internal static void OnDispatcherMessageReceive(Message msg)
    {
        if (!DispatcherMessagesReceivedCounter.Enabled)
            return;
        var context = RuntimeContext.Current;
        var aggregator = DispatcherMessagesReceivedCounterAggregatorGroup.FindOrCreate(
            "Context", context is null ? null : "Activation",
            "Direction", msg.Direction.ToString()
        );
        aggregator.Add(1);
    }

    internal static void OnDispatcherMessageProcessedOk(Message msg)
    {
        if (!DispatcherMessagesProcessedCounter.Enabled)
            return;
        var aggregator = DispatcherMessagesProcessedCounterAggregatorGroup.FindOrCreate(
            "Direction", msg.Direction.ToString(),
            "Status", "Ok"
        );
        aggregator.Add(1);
    }

    internal static void OnDispatcherMessageProcessedError(Message msg)
    {
        if (!DispatcherMessagesProcessedCounter.Enabled)
            return;
        var aggregator = DispatcherMessagesProcessedCounterAggregatorGroup.FindOrCreate(
            "Direction", msg.Direction.ToString(),
            "Status", "Error"
        );
        aggregator.Add(1);
    }

    internal static void OnDispatcherMessageForwared(Message msg)
    {
        dispatcherMessagesForwardedCounter.Add(1);
    }

    internal static void OnImaMessageReceived(Message msg)
    {
        imaReceivedCounterAggregator.Add(1);
    }

    internal static void OnImaMessageEnqueued(IGrainContext context)
    {
        KeyValuePair<string, object> tag;
        if (context == null)
        {
            tag = new KeyValuePair<string, object>("Context", "ToNull");
        }
        else if (context is ISystemTargetBase)
        {
            tag = new KeyValuePair<string, object>("Context", "ToSystemTarget");
        }
        else
        {
            tag = new KeyValuePair<string, object>("Context", "ToActivation");
        }
        imaEnqueuedCounter.Add(1, tag);
    }

    internal static ObservableGauge<long> ActivationDataAll;
    internal static void RegisterActivationDataAllObserve(Func<long> observeValue)
    {
        ActivationDataAll = Instruments.Meter.CreateObservableGauge(InstrumentNames.MESSAGING_PROCESSING_ACTIVATION_DATA_ALL, observeValue);
    }

}
