using System;


namespace Orleans.Runtime
{
    internal class MessagingProcessingStatisticsGroup
    {
        private static CounterStatistic[] dispatcherMessagesProcessedOkPerDirection;
        private static CounterStatistic[] dispatcherMessagesProcessedErrorsPerDirection;
        private static CounterStatistic[] dispatcherMessagesProcessedReRoutePerDirection;
        private static CounterStatistic[] dispatcherMessagesProcessingReceivedPerDirection;
        private static CounterStatistic dispatcherMessagesProcessedTotal;
        private static CounterStatistic dispatcherMessagesReceivedTotal;
        private static CounterStatistic[] dispatcherReceivedByContext;
      
        private static CounterStatistic igcMessagesForwarded;
        private static CounterStatistic igcMessagesResent;
        private static CounterStatistic igcMessagesReRoute;

        private static CounterStatistic imaReceived;
        private static CounterStatistic[] imaEnqueuedByContext;


        internal static void Init()
        {
            dispatcherMessagesProcessedOkPerDirection = new CounterStatistic[Enum.GetValues(typeof(Message.Directions)).Length];
            foreach (var direction in Enum.GetValues(typeof(Message.Directions)))
            {
                dispatcherMessagesProcessedOkPerDirection[(int)direction] = CounterStatistic.FindOrCreate(
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_OK_PER_DIRECTION, Enum.GetName(typeof(Message.Directions), direction)));
            }
            dispatcherMessagesProcessedErrorsPerDirection = new CounterStatistic[Enum.GetValues(typeof(Message.Directions)).Length];
            foreach (var direction in Enum.GetValues(typeof(Message.Directions)))
            {
                dispatcherMessagesProcessedErrorsPerDirection[(int)direction] = CounterStatistic.FindOrCreate(
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_ERRORS_PER_DIRECTION, Enum.GetName(typeof(Message.Directions), direction)));
            }
            dispatcherMessagesProcessedReRoutePerDirection = new CounterStatistic[Enum.GetValues(typeof(Message.Directions)).Length];
            foreach (var direction in Enum.GetValues(typeof(Message.Directions)))
            {
                dispatcherMessagesProcessedReRoutePerDirection[(int)direction] = CounterStatistic.FindOrCreate(
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_REROUTE_PER_DIRECTION, Enum.GetName(typeof(Message.Directions), direction)));
            }

            dispatcherMessagesProcessingReceivedPerDirection = new CounterStatistic[Enum.GetValues(typeof(Message.Directions)).Length];
            foreach (var direction in Enum.GetValues(typeof(Message.Directions)))
            {
                dispatcherMessagesProcessingReceivedPerDirection[(int)direction] = CounterStatistic.FindOrCreate(
                    new StatisticName(StatisticNames.MESSAGING_DISPATCHER_RECEIVED_PER_DIRECTION, Enum.GetName(typeof(Message.Directions), direction)));
            }
            dispatcherMessagesProcessedTotal = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_DISPATCHER_PROCESSED_TOTAL);
            dispatcherMessagesReceivedTotal = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_DISPATCHER_RECEIVED_TOTAL);

            igcMessagesForwarded = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_IGC_FORWARDED);
            igcMessagesResent = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_IGC_RESENT);
            igcMessagesReRoute = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_IGC_REROUTE);

            imaReceived = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_IMA_RECEIVED);
            imaEnqueuedByContext = new CounterStatistic[3];
            imaEnqueuedByContext[0] = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_IMA_ENQUEUED_TO_NULL);
            imaEnqueuedByContext[1] = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_IMA_ENQUEUED_TO_SYSTEM_TARGET);
            imaEnqueuedByContext[2] = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_IMA_ENQUEUED_TO_ACTIVATION);

            dispatcherReceivedByContext = new CounterStatistic[2];
            dispatcherReceivedByContext[0] = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_DISPATCHER_RECEIVED_ON_NULL);
            dispatcherReceivedByContext[1] = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_DISPATCHER_RECEIVED_ON_ACTIVATION);
        }

        internal static void OnDispatcherMessageReceive(Message msg)
        {
            ISchedulingContext context = RuntimeContext.Current != null ? RuntimeContext.Current.ActivationContext : null;
            dispatcherMessagesProcessingReceivedPerDirection[(int)msg.Direction].Increment();
            dispatcherMessagesReceivedTotal.Increment();
            if (context == null)
            {
                dispatcherReceivedByContext[0].Increment();
            }
            else if (context.ContextType == SchedulingContextType.Activation)
            {
                dispatcherReceivedByContext[1].Increment();
            }
        }

        internal static void OnDispatcherMessageProcessedOk(Message msg)
        {
            dispatcherMessagesProcessedOkPerDirection[(int)msg.Direction].Increment();
            dispatcherMessagesProcessedTotal.Increment();
        }

        internal static void OnDispatcherMessageProcessedError(Message msg, string reason)
        {
            dispatcherMessagesProcessedErrorsPerDirection[(int)msg.Direction].Increment();
            dispatcherMessagesProcessedTotal.Increment();
        }

        internal static void OnDispatcherMessageReRouted(Message msg)
        {
            dispatcherMessagesProcessedReRoutePerDirection[(int)msg.Direction].Increment();
            dispatcherMessagesProcessedTotal.Increment();
        }

        internal static void OnIgcMessageForwared(Message msg)
        {
            igcMessagesForwarded.Increment();
        }

        internal static void OnIgcMessageResend(Message msg)
        {
            igcMessagesResent.Increment();
        }

        internal static void OnIgcMessageReRoute(Message msg)
        {
            igcMessagesReRoute.Increment();
        }

        internal static void OnImaMessageReceived(Message msg)
        {
            imaReceived.Increment();
        }

        internal static void OnImaMessageEnqueued(ISchedulingContext context)
        {
            if (context == null)
            {
                imaEnqueuedByContext[0].Increment();
            }
            else if (context.ContextType == SchedulingContextType.SystemTarget)
            {
                imaEnqueuedByContext[1].Increment();
            }
            else if (context.ContextType == SchedulingContextType.Activation)
            {
                imaEnqueuedByContext[2].Increment();
            }
        }
    }
}

