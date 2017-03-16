using System;
using System.Collections.Concurrent;
using System.Collections.Generic;


namespace Orleans.Runtime.Messaging
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1711:IdentifiersShouldNotHaveIncorrectSuffix")]
    internal class InboundMessageQueue : IInboundMessageQueue
    {
        private readonly Action<Message>[] messageHandlers;

        private readonly Action<Message>[] messageShortCircuitHandlers;
        private readonly Logger log;
        private readonly QueueTrackingStatistic[] queueTracking;
        private readonly List<Message>[] msgs; 
            
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
            queueTracking = new QueueTrackingStatistic[n];
            msgs = new List<Message>[n];
            messageHandlers = new Action<Message>[n];
            messageShortCircuitHandlers =new Action<Message>[n];
            for (int g = 0; g < n; g++)
            {
                msgs[g] = new List<Message>();
                var ind = g;
                messageHandlers[g] = message =>
                {
                    msgs[ind].Add(message);
                };
            }
            int i = 0;
            foreach (var category in Enum.GetValues(typeof(Message.Categories)))
            {
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
        public void PostShortCircuitMessage(Message msg)
        {
#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectQueueStats)
            {
                queueTracking[(int)msg.Category].OnEnQueueRequest(1, messageQueues[(int)msg.Category].Count, msg);
            }
#endif
            messageShortCircuitHandlers[(int)msg.Category](msg);

            if (log.IsVerbose3) log.Verbose3("Queued incoming {0} message", msg.Category.ToString());
        }

        public void AddTargetBlock(Message.Categories type, Action<Message> actionBlock)
        {
            // todo: make it pretty
            msgs[(int)type].ForEach(actionBlock);
            msgs[(int)type] = new List<Message>();
            messageHandlers[(int) type] = actionBlock;
        }

        public void AddShortCicruitTargetBlock(Message.Categories type, Action<Message> actionBlock)
        {
            messageShortCircuitHandlers[(int)type] = actionBlock;
        }


//#if TRACK_DETAILED_STATS
//                if (StatisticsCollector.CollectQueueStats)
//                {
//                    queueTracking[(int)msg.Category].OnDeQueueRequest(msg);
//                }
//#endif
    }
}
