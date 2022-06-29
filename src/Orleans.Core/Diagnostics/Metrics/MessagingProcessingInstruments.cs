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
    private static readonly CounterAggregator ImaMessageEnqueuedNullContext;
    private static readonly CounterAggregator ImaMessageEnqueuedSystemTarget;
    private static readonly CounterAggregator ImaMessageEnqueuedGrain;
    private static readonly CounterAggregator[] DispatcherMessagesReceivedCounters_NullContext;
    private static readonly CounterAggregator[] DispatcherMessagesReceivedCounters_Grain;

    private static readonly CounterAggregator[] DispatcherMessagesProcessedCounters_Ok;
    private static readonly CounterAggregator[] DispatcherMessagesProcessedCounters_Error;

    static MessagingProcessingInstruments()
    {
        ImaMessageEnqueuedNullContext = ImaEnqueuedCounterAggregatorGroup.FindOrCreate(new("Context", "ToNull"));
        ImaMessageEnqueuedSystemTarget = ImaEnqueuedCounterAggregatorGroup.FindOrCreate(new("Context", "ToSystemTarget"));
        ImaMessageEnqueuedGrain = ImaEnqueuedCounterAggregatorGroup.FindOrCreate(new("Context", "ToGrain"));
        var directionEnumValues = Enum.GetValues<Message.Directions>();
        DispatcherMessagesReceivedCounters_NullContext = new CounterAggregator[directionEnumValues.Length + 1];
        DispatcherMessagesReceivedCounters_Grain = new CounterAggregator[directionEnumValues.Length + 1];
        DispatcherMessagesProcessedCounters_Ok = new CounterAggregator[directionEnumValues.Length + 1];
        DispatcherMessagesProcessedCounters_Error = new CounterAggregator[directionEnumValues.Length + 1];
        foreach (var value in directionEnumValues)
        {
            DispatcherMessagesReceivedCounters_NullContext[(int)value] = DispatcherMessagesReceivedCounterAggregatorGroup.FindOrCreate(new("Context", "None", "Direction", value.ToString()));
            DispatcherMessagesReceivedCounters_Grain[(int)value] = DispatcherMessagesReceivedCounterAggregatorGroup.FindOrCreate(new("Context", "Grain", "Direction", value.ToString()));

            DispatcherMessagesProcessedCounters_Ok[(int)value] = DispatcherMessagesProcessedCounterAggregatorGroup.FindOrCreate(new("Direction", value.ToString(), "Status", "Ok"));
            DispatcherMessagesProcessedCounters_Error[(int)value] = DispatcherMessagesProcessedCounterAggregatorGroup.FindOrCreate(new("Direction", value.ToString(), "Status", "Error"));
        }
    }

    internal static void OnDispatcherMessageReceive(Message msg)
    {
        if (!DispatcherMessagesReceivedCounter.Enabled)
            return;
        var context = RuntimeContext.Current;
        var counters = context switch
        {
            null => DispatcherMessagesReceivedCounters_NullContext,
            _ => DispatcherMessagesReceivedCounters_Grain,
        };
        counters[(int)msg.Direction].Add(1);
    }

    internal static void OnDispatcherMessageProcessedOk(Message msg)
    {
        if (DispatcherMessagesProcessedCounter.Enabled)
        {
            DispatcherMessagesProcessedCounters_Ok[(int)msg.Direction].Add(1);
        }
    }

    internal static void OnDispatcherMessageProcessedError(Message msg)
    {
        if (DispatcherMessagesProcessedCounter.Enabled)
        {
            DispatcherMessagesProcessedCounters_Error[(int)msg.Direction].Add(1);
        }
    }

    internal static void OnDispatcherMessageForwared(Message msg)
    {
        if (DispatcherMessagesForwardedCounter.Enabled)
        {
            DispatcherMessagesForwardedCounterAggregator.Add(1);
        }
    }

    internal static void OnImaMessageReceived(Message msg)
    {
        if (ImaReceivedCounter.Enabled)
        {
            ImaReceivedCounterAggregator.Add(1);
        }
    }

    internal static void OnImaMessageEnqueued(IGrainContext context)
    {
        if (!ImaEnqueuedCounter.Enabled)
        {
            return;
        }

        switch (context)
        {
            case null:
                ImaMessageEnqueuedNullContext.Add(1);
                break;
            case ISystemTargetBase:
                ImaMessageEnqueuedSystemTarget.Add(1);
                break;
            default:
                ImaMessageEnqueuedGrain.Add(1);
                break;
        }
    }

    internal static ObservableGauge<long> ActivationDataAll;

    internal static void RegisterActivationDataAllObserve(Func<long> observeValue)
    {
        ActivationDataAll = Instruments.Meter.CreateObservableGauge(InstrumentNames.MESSAGING_PROCESSING_ACTIVATION_DATA_ALL, observeValue);
    }
}
