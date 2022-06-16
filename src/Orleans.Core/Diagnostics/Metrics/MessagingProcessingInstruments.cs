using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal static class MessagingProcessingInstruments
{
    private static readonly Counter<long> dispatcherMessagesProcessedCounter = Instruments.Meter.CreateCounter<long>(StatisticNames.MESSAGING_DISPATCHER_PROCESSED);
    private static readonly Counter<long> dispatcherMessagesReceivedCounter = Instruments.Meter.CreateCounter<long>(StatisticNames.MESSAGING_DISPATCHER_RECEIVED);
    private static readonly Counter<long> dispatcherMessagesForwardedCounter = Instruments.Meter.CreateCounter<long>(StatisticNames.MESSAGING_DISPATCHER_FORWARDED);
    private static readonly Counter<long> imaReceivedCounter = Instruments.Meter.CreateCounter<long>(StatisticNames.MESSAGING_IMA_RECEIVED);
    private static readonly Counter<long> imaEnqueuedCounter = Instruments.Meter.CreateCounter<long>(StatisticNames.MESSAGING_IMA_ENQUEUED);

    internal static void OnDispatcherMessageReceive(Message msg)
    {
        var context = RuntimeContext.Current;
        dispatcherMessagesReceivedCounter.Add(1, new KeyValuePair<string, object>("Context", context is null ? null : "Activation"), new KeyValuePair<string, object>("Direction", msg.Direction));
    }

    internal static void OnDispatcherMessageProcessedOk(Message msg)
    {
        // TODO: avoid allocation?
        dispatcherMessagesProcessedCounter.Add(1, new KeyValuePair<string, object>("Direction", msg.Direction), new KeyValuePair<string, object>("Status", "Ok"));
    }

    internal static void OnDispatcherMessageProcessedError(Message msg)
    {
        dispatcherMessagesProcessedCounter.Add(1, new KeyValuePair<string, object>("Direction", msg.Direction), new KeyValuePair<string, object>("Status", "Error"));
    }

    internal static void OnDispatcherMessageForwared(Message msg)
    {
        dispatcherMessagesForwardedCounter.Add(1);
    }

    internal static void OnImaMessageReceived(Message msg)
    {
        imaReceivedCounter.Add(1);
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
        ActivationDataAll = Instruments.Meter.CreateObservableGauge(StatisticNames.MESSAGING_PROCESSING_ACTIVATION_DATA_ALL, observeValue);
    }
}
