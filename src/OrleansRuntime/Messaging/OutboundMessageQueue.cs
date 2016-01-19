using System;
using System.Linq;
using System.Threading;

using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.Messaging
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1711:IdentifiersShouldNotHaveIncorrectSuffix")]
    internal sealed class OutboundMessageQueue : IOutboundMessageQueue
    {
        private readonly Lazy<SiloMessageSender>[] senders;
        private readonly SiloMessageSender pingSender;
        private readonly SiloMessageSender systemSender;
        private readonly MessageCenter messageCenter;
        private readonly TraceLogger logger;
        private bool stopped;

        public int Count
        {
            get
            {
                int n = senders.Where(sender => sender.IsValueCreated).Sum(sender => sender.Value.Count);
                n += systemSender.Count + pingSender.Count;
                return n;
            }
        }

        internal const string QUEUED_TIME_METADATA = "QueuedTime";

        internal OutboundMessageQueue(MessageCenter mc, IMessagingConfiguration config)
        {
            messageCenter = mc;
            pingSender = new SiloMessageSender("PingSender", messageCenter);
            systemSender = new SiloMessageSender("SystemSender", messageCenter);
            senders = new Lazy<SiloMessageSender>[config.SiloSenderQueues];

            for (int i = 0; i < senders.Length; i++)
            {
                int capture = i;
                senders[capture] = new Lazy<SiloMessageSender>(() =>
                {
                    var sender = new SiloMessageSender("AppMsgsSender_" + capture, messageCenter);
                    sender.Start();
                    return sender;
                }, LazyThreadSafetyMode.ExecutionAndPublication);
            }
            logger = TraceLogger.GetLogger("Messaging.OutboundMessageQueue");
            stopped = false;
        }

        public void SendMessage(Message msg)
        {
            if (msg == null) throw new ArgumentNullException("msg", "Can't send a null message.");

            if (stopped)
            {
                logger.Info(ErrorCode.Runtime_Error_100112, "Message was queued for sending after outbound queue was stopped: {0}", msg);
                return;
            }

            // Don't process messages that have already timed out
            if (msg.IsExpired)
            {
                msg.DropExpiredMessage(MessagingStatisticsGroup.Phase.Send);
                return;
            }

            if (!msg.ContainsMetadata(QUEUED_TIME_METADATA))
            {
                msg.SetMetadata(QUEUED_TIME_METADATA, DateTime.UtcNow);
            }

            // First check to see if it's really destined for a proxied client, instead of a local grain.
            if (messageCenter.IsProxying && messageCenter.TryDeliverToProxy(msg))
            {
                return;
            }

            if (!msg.ContainsHeader(Message.Header.TARGET_SILO))
            {
                logger.Error(ErrorCode.Runtime_Error_100113, "Message does not have a target silo: " + msg + " -- Call stack is: " + (new System.Diagnostics.StackTrace()));
                messageCenter.SendRejection(msg, Message.RejectionTypes.Unrecoverable, "Message to be sent does not have a target silo");
                return;
            }

            if (Message.WriteMessagingTraces)
                msg.AddTimestamp(Message.LifecycleTag.EnqueueOutgoing);

            // Shortcut messages to this silo
            if (msg.TargetSilo.Equals(messageCenter.MyAddress))
            {
                if (logger.IsVerbose3) logger.Verbose3("Message has been looped back to this silo: {0}", msg);
                MessagingStatisticsGroup.LocalMessagesSent.Increment();
                messageCenter.InboundQueue.PostMessage(msg);
            }
            else
            {
                if (stopped)
                {
                    logger.Info(ErrorCode.Runtime_Error_100115, "Message was queued for sending after outbound queue was stopped: {0}", msg);
                    return;
                }

                // check for simulation of lost messages
                if(Silo.CurrentSilo.TestHook.ShouldDrop(msg))
                {
                    logger.Info(ErrorCode.Messaging_SimulatedMessageLoss, "Message blocked by test");
                    messageCenter.SendRejection(msg, Message.RejectionTypes.Unrecoverable, "Message blocked by test");
                }

                // Prioritize system messages
                switch (msg.Category)
                {
                    case Message.Categories.Ping:
                        pingSender.QueueRequest(msg);
                        break;

                    case Message.Categories.System:
                        systemSender.QueueRequest(msg);
                        break;

                    default:
                    {
                        int index = Math.Abs(msg.TargetSilo.GetConsistentHashCode()) % senders.Length;
                        senders[index].Value.QueueRequest(msg);
                        break;
                    }
                }
            }
        }

        public void Start()
        {
            pingSender.Start();
            systemSender.Start();
            stopped = false;
        }

        public void Stop()
        {
            stopped = true;
            foreach (var sender in senders)
            {
                if (sender.IsValueCreated)
                    sender.Value.Stop();                
            }
            systemSender.Stop();
            pingSender.Stop();
        }

        #region IDisposable Members

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA1816:CallGCSuppressFinalizeCorrectly")]
        public void Dispose()
        {
            stopped = true;
            foreach (var sender in senders)
            {
                sender.Value.Stop();
                sender.Value.Dispose();
            }
            systemSender.Stop();
            pingSender.Stop();
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
