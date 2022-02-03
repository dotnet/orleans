using System;


namespace Orleans.Runtime
{
    internal class MessagingProcessingStatisticsGroup
    {
        private static CounterStatistic[] dispatcherMessagesProcessedOkPerDirection;
        private static CounterStatistic[] dispatcherMessagesProcessedErrorsPerDirection;
        private static CounterStatistic[] dispatcherMessagesProcessingReceivedPerDirection;
        private static CounterStatistic dispatcherMessagesProcessedTotal;
        private static CounterStatistic dispatcherMessagesReceivedTotal;
        private static CounterStatistic[] dispatcherReceivedByContext;
      
        private static CounterStatistic dispatcherMessagesForwarded;

        private static CounterStatistic imaReceived;
        private static CounterStatistic[] imaEnqueuedByContext;


        internal static void Init()
        {
            dispatcherMessagesProcessedOkPerDirection ??= new CounterStatistic[Enum.GetValues(typeof(Message.Directions)).Length];
            foreach (var direction in Enum.GetValues(typeof(Message.Directions)))
            {
                dispatcherMessagesProcessedOkPerDirection[(byte)direction] = CounterStatistic.FindOrCreate(
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_OK_PER_DIRECTION, Enum.GetName(typeof(Message.Directions), direction)));
            }
            dispatcherMessagesProcessedErrorsPerDirection ??= new CounterStatistic[Enum.GetValues(typeof(Message.Directions)).Length];
            foreach (var direction in Enum.GetValues(typeof(Message.Directions)))
            {
                dispatcherMessagesProcessedErrorsPerDirection[(byte)direction] = CounterStatistic.FindOrCreate(
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_ERRORS_PER_DIRECTION, Enum.GetName(typeof(Message.Directions), direction)));
            }
            dispatcherMessagesProcessingReceivedPerDirection ??= new CounterStatistic[Enum.GetValues(typeof(Message.Directions)).Length];
            foreach (var direction in Enum.GetValues(typeof(Message.Directions)))
            {
                dispatcherMessagesProcessingReceivedPerDirection[(byte)direction] = CounterStatistic.FindOrCreate(
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_RECEIVED_PER_DIRECTION, Enum.GetName(typeof(Message.Directions), direction)));
            }
            dispatcherMessagesProcessedTotal = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_TOTAL);
            dispatcherMessagesReceivedTotal = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_DISPATCHER_RECEIVED_TOTAL);

            dispatcherMessagesForwarded = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_DISPATCHER_FORWARDED);

            imaReceived = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_IMA_RECEIVED);
            imaEnqueuedByContext ??= new CounterStatistic[3];
            imaEnqueuedByContext[0] = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_IMA_ENQUEUED_TO_NULL);
            imaEnqueuedByContext[1] = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_IMA_ENQUEUED_TO_SYSTEM_TARGET);
            imaEnqueuedByContext[2] = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_IMA_ENQUEUED_TO_ACTIVATION);

            dispatcherReceivedByContext ??= new CounterStatistic[2];
            dispatcherReceivedByContext[0] = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_DISPATCHER_RECEIVED_ON_NULL);
            dispatcherReceivedByContext[1] = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_DISPATCHER_RECEIVED_ON_ACTIVATION);
        }

        internal static void OnDispatcherMessageReceive(Message msg)
        {
            var context = RuntimeContext.Current;
            dispatcherMessagesProcessingReceivedPerDirection[(byte)msg.Direction].Increment();
            dispatcherMessagesReceivedTotal.Increment();
            if (context == null)
            {
                dispatcherReceivedByContext[0].Increment();
            }
            else if (context is IGrainContext)
            {
                dispatcherReceivedByContext[1].Increment();
            }
        }

        internal static void OnDispatcherMessageProcessedOk(Message msg)
        {
            dispatcherMessagesProcessedOkPerDirection[(byte)msg.Direction].Increment();
            dispatcherMessagesProcessedTotal.Increment();
        }

        internal static void OnDispatcherMessageProcessedError(Message msg)
        {
            dispatcherMessagesProcessedErrorsPerDirection[(byte)msg.Direction].Increment();
            dispatcherMessagesProcessedTotal.Increment();
        }

        internal static void OnDispatcherMessageForwared(Message msg)
        {
            dispatcherMessagesForwarded.Increment();
        }

        internal static void OnImaMessageReceived(Message msg)
        {
            imaReceived.Increment();
        }

        internal static void OnImaMessageEnqueued(IGrainContext context)
        {
            if (context == null)
            {
                imaEnqueuedByContext[0].Increment();
            }
            else if (context is ISystemTargetBase)
            {
                imaEnqueuedByContext[1].Increment();
            }
            else if (context is IGrainContext)
            {
                imaEnqueuedByContext[2].Increment();
            }
        }
    }
}

