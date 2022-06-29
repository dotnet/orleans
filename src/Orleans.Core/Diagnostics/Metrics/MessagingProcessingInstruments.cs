using System;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal static class MessagingProcessingInstruments
{
    private static readonly CounterAggregatorGroup DispatcherMessagesProcessedCounterAggregatorGroup = new();
    private static readonly ObservableCounter<long> DispatcherMessagesProcessedCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.MESSAGING_DISPATCHER_PROCESSED, DispatcherMessagesProcessedCounterAggregatorGroup.Collect);

    private static readonly CounterAggregatorGroup DispatcherMessagesReceivedCounterAggregatorGroup = new();
    private static readonly ObservableCounter<long> DispatcherMessagesReceivedCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.MESSAGING_DISPATCHER_RECEIVED, DispatcherMessagesReceivedCounterAggregatorGroup.Collect);

    private static readonly CounterAggregator DispatcherMessagesForwardedCounterAggregator = new();
    private static readonly ObservableCounter<long> DispatcherMessagesForwardedCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.MESSAGING_DISPATCHER_FORWARDED, DispatcherMessagesForwardedCounterAggregator.Collect);
    private static readonly CounterAggregator ImaReceivedCounterAggregator = new();
    private static readonly ObservableCounter<long> ImaReceivedCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.MESSAGING_IMA_RECEIVED, ImaReceivedCounterAggregator.Collect);
    private static readonly CounterAggregatorGroup ImaEnqueuedCounterAggregatorGroup = new();
    private static readonly ObservableCounter<long> ImaEnqueuedCounter = Instruments.Meter.CreateObservableCounter<long>(InstrumentNames.MESSAGING_IMA_ENQUEUED, ImaEnqueuedCounterAggregatorGroup.Collect);

    internal static void OnDispatcherMessageReceive(Message msg)
    {
        if (!DispatcherMessagesReceivedCounter.Enabled)
            return;
        var context = RuntimeContext.Current;
        DispatcherMessagesReceivedCounterAggregatorGroup.Add(1,
            "Context", context is null ? null : "Activation",
            "Direction", msg.Direction.ToString()
        );
    }

    internal static void OnDispatcherMessageProcessedOk(Message msg)
    {
        if (DispatcherMessagesProcessedCounter.Enabled)
            DispatcherMessagesProcessedCounterAggregatorGroup.Add(1,
                "Direction", msg.Direction.ToString(),
                "Status", "Ok"
            );
    }

    internal static void OnDispatcherMessageProcessedError(Message msg)
    {
        if (DispatcherMessagesProcessedCounter.Enabled)
            DispatcherMessagesProcessedCounterAggregatorGroup.Add(1,
                "Direction", msg.Direction.ToString(),
                "Status", "Error"
            );
    }

    internal static void OnDispatcherMessageForwared(Message msg)
    {
        if (DispatcherMessagesForwardedCounter.Enabled)
            DispatcherMessagesForwardedCounterAggregator.Add(1);
    }

    internal static void OnImaMessageReceived(Message msg)
    {
        if (ImaReceivedCounter.Enabled)
            ImaReceivedCounterAggregator.Add(1);
    }

    internal static void OnImaMessageEnqueued(IGrainContext context)
    {
        if (!ImaEnqueuedCounter.Enabled)
            return;
        if (context == null)
        {
            ImaEnqueuedCounterAggregatorGroup.Add(1,
                "Context", "ToNull"
            );
        }
        else if (context is ISystemTargetBase)
        {
            ImaEnqueuedCounterAggregatorGroup.Add(1,
                "Context", "ToSystemTarget"
            );
        }
        else
        {
            ImaEnqueuedCounterAggregatorGroup.Add(1,
                "Context", "ToActivation"
            );
        }
    }

    internal static ObservableGauge<long> ActivationDataAll;
    internal static void RegisterActivationDataAllObserve(Func<long> observeValue)
    {
        ActivationDataAll = Instruments.Meter.CreateObservableGauge(InstrumentNames.MESSAGING_PROCESSING_ACTIVATION_DATA_ALL, observeValue);
    }

}
