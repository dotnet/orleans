using System;
using System.Collections.Concurrent;
using System.Threading.Tasks.Dataflow;


namespace Orleans.Runtime.Messaging
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1711:IdentifiersShouldNotHaveIncorrectSuffix")]
    internal class InboundMessageQueue : IInboundMessageQueue
    {
        private readonly ITargetBlock<Message>[] messageQueues;
        private readonly Action<Message>[] messageHandlers;
        private readonly Logger log;
        private readonly QueueTrackingStatistic[] queueTracking;

        public int Count
        {
            get
            {
                int n = 0;
                // currently doesn't being used

                //foreach (var queue in messageQueues)
                //    n += queue.Count;

                return n;
            }
        }

        internal InboundMessageQueue()
        {
            int n = Enum.GetValues(typeof(Message.Categories)).Length;
            messageQueues = new ITargetBlock<Message>[n];
            queueTracking = new QueueTrackingStatistic[n];
            messageHandlers = new Action<Message>[n];
            for (int g = 0; g < n; g++)
            {
                messageHandlers[g]= new Action<Message>(message => log.Error(0, "Message recieved before start"));
            }
            int i = 0;
            foreach (var category in Enum.GetValues(typeof(Message.Categories)))
            {
                messageQueues[i] = new BufferBlock<Message>();
                if (StatisticsCollector.CollectQueueStats)
                {
                    var queueName = "IncomingMessageAgent." + category;
                    queueTracking[i] = new QueueTrackingStatistic(queueName);
                    queueTracking[i].OnStartExecution();
                }
                i++;
            }
            log = LogManager.GetLogger("Orleans.Messaging.InboundMessageQueue");
        }

        public void Stop()
        {
            if (messageQueues == null) return;
            foreach (var q in messageQueues)
                q.Complete();
            
            if (!StatisticsCollector.CollectQueueStats) return;

            foreach (var q in queueTracking)
                q.OnStopExecution();
        }

        public void PostMessage(Message msg)
        {
#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectQueueStats)
            {
                queueTracking[(int)msg.Category].OnEnQueueRequest(1, messageQueues[(int)msg.Category].Count, msg);
            }
#endif
            messageHandlers[(int)msg.Category](msg);
           
            if (log.IsVerbose3) log.Verbose3("Queued incoming {0} message", msg.Category.ToString());
        }

        public void AddTargetBlock(Message.Categories type, Action<Message> actionBlock)
        {
            messageHandlers[(int) type] = actionBlock;
        }


//#if TRACK_DETAILED_STATS
//                if (StatisticsCollector.CollectQueueStats)
//                {
//                    queueTracking[(int)msg.Category].OnDeQueueRequest(msg);
//                }
//#endif
    }
}
