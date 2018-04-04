using System;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Messaging
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1711:IdentifiersShouldNotHaveIncorrectSuffix")]
    internal sealed class OutboundMessageQueue : IOutboundMessageQueue
    {
        private readonly Lazy<SiloMessageSender>[] senders;
        private readonly SiloMessageSender pingSender;
        private readonly SiloMessageSender systemSender;
        private readonly MessageCenter messageCenter;
        private readonly ILogger logger;
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

        internal OutboundMessageQueue(MessageCenter mc, IOptions<SiloMessagingOptions> options, SerializationManager serializationManager, ExecutorService executorService, ILoggerFactory loggerFactory)
        {
            messageCenter = mc;
            pingSender = new SiloMessageSender("PingSender", messageCenter, serializationManager, executorService, loggerFactory);
            systemSender = new SiloMessageSender("SystemSender", messageCenter, serializationManager, executorService, loggerFactory);
            senders = new Lazy<SiloMessageSender>[options.Value.SiloSenderQueues];

            for (int i = 0; i < senders.Length; i++)
            {
                int capture = i;
                senders[capture] = new Lazy<SiloMessageSender>(() =>
                {
                    var sender = new SiloMessageSender("AppMsgsSender_" + capture, messageCenter, serializationManager, executorService, loggerFactory);
                    sender.Start();
                    return sender;
                }, LazyThreadSafetyMode.ExecutionAndPublication);
            }
            logger = loggerFactory.CreateLogger<OutboundMessageQueue>();
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

            if (!msg.QueuedTime.HasValue)
            {
                msg.QueuedTime = DateTime.UtcNow;
            }

            // First check to see if it's really destined for a proxied client, instead of a local grain.
            if (messageCenter.IsProxying && messageCenter.TryDeliverToProxy(msg))
            {
                return;
            }

            if (msg.TargetSilo == null)
            {
                logger.Error(ErrorCode.Runtime_Error_100113, "Message does not have a target silo: " + msg + " -- Call stack is: " + Utils.GetStackTrace());
                messageCenter.SendRejection(msg, Message.RejectionTypes.Unrecoverable, "Message to be sent does not have a target silo");
                return;
            }
            
            if(!messageCenter.TrySendLocal(msg))
            {
                if (stopped)
                {
                    logger.Info(ErrorCode.Runtime_Error_100115, "Message was queued for sending after outbound queue was stopped: {0}", msg);
                    return;
                }

                // check for simulation of lost messages
                if(messageCenter?.ShouldDrop?.Invoke(msg) == true)
                {
                    logger.Info(ErrorCode.Messaging_SimulatedMessageLoss, "Message blocked by test");
                    messageCenter.SendRejection(msg, Message.RejectionTypes.Unrecoverable, "Message blocked by test");
                    return;
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
