using System;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace Orleans.Runtime.Messaging
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1711:IdentifiersShouldNotHaveIncorrectSuffix")]
    internal sealed class OutboundMessageQueue : IDisposable
    {
        private readonly MessageCenter messageCenter;
        private readonly ConnectionManager connectionManager;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly MessagingTrace messagingTrace;
        private readonly ILogger logger;
        private bool stopped;

        public int GetCount()
        {
            int n = GetApplicationMessageCount();
            return n; // TODO
        }

        public int GetApplicationMessageCount()
        {
            return 0; // TODO
        }

        internal const string QUEUED_TIME_METADATA = "QueuedTime";

        internal OutboundMessageQueue(
            MessageCenter mc,
            ILogger<OutboundMessageQueue> logger,
            ConnectionManager senderManager,
            ISiloStatusOracle siloStatusOracle,
            MessagingTrace messagingTrace)
        {
            messageCenter = mc;
            this.connectionManager = senderManager;
            this.siloStatusOracle = siloStatusOracle;
            this.messagingTrace = messagingTrace;
            this.logger = logger;
            stopped = false;
        }

        public void SendMessage(Message msg)
        {
            if (msg is null) throw new ArgumentNullException("msg", "Can't send a null message.");

            if (stopped)
            {
                logger.LogInformation((int)ErrorCode.Runtime_Error_100115, "Message was queued for sending after outbound queue was stopped: {Message}", msg);
                messageCenter.SendRejection(msg, Message.RejectionTypes.Unrecoverable, "Message was queued for sending after outbound queue was stopped");
                return;
            }

            // Don't process messages that have already timed out
            if (msg.IsExpired)
            {
                this.messagingTrace.OnDropExpiredMessage(msg, MessagingStatisticsGroup.Phase.Send);
                return;
            }

            if (!msg.QueuedTime.HasValue)
            {
                msg.QueuedTime = DateTime.UtcNow;
            }

            // First check to see if it's really destined for a proxied client, instead of a local grain.
            if (messageCenter.TryDeliverToProxy(msg))
            {
                // Message was successfully delivered to the proxy.
                return;
            }

            if (msg.TargetSilo == null)
            {
                logger.LogError((int)ErrorCode.Runtime_Error_100113, "Message does not have a target silo: " + msg + " -- Call stack is: " + Utils.GetStackTrace());
                messageCenter.SendRejection(msg, Message.RejectionTypes.Unrecoverable, "Message to be sent does not have a target silo");
                return;
            }

            messagingTrace.OnSendMessage(msg);
            if (!messageCenter.TrySendLocal(msg))
            {
                if (stopped)
                {
                    logger.LogInformation((int)ErrorCode.Runtime_Error_100115, "Message was queued for sending after outbound queue was stopped: {Message}", msg);
                    messageCenter.SendRejection(msg, Message.RejectionTypes.Unrecoverable, "Message was queued for sending after outbound queue was stopped");
                    return;
                }

                // check for simulation of lost messages
                if (messageCenter.ShouldDrop?.Invoke(msg) == true)
                {
                    logger.LogInformation((int)ErrorCode.Messaging_SimulatedMessageLoss, "Message blocked by test");
                    messageCenter.SendRejection(msg, Message.RejectionTypes.Unrecoverable, "Message blocked by test");
                    return;
                }

                if (this.siloStatusOracle.IsDeadSilo(msg.TargetSilo))
                {
                    this.messagingTrace.OnRejectSendMessageToDeadSilo(this.messageCenter.MyAddress, msg);
                    this.messageCenter.SendRejection(msg, Message.RejectionTypes.Transient, "Target silo is known to be dead");
                    return;
                }

                var senderTask = this.connectionManager.GetConnection(msg.TargetSilo);
                if (senderTask.IsCompletedSuccessfully)
                {
                    var sender = senderTask.Result;
                    sender.Send(msg);
                }
                else
                {
                    _ = SendAsync(senderTask, msg);

                    async Task SendAsync(ValueTask<Connection> c, Message m)
                    {
                        try
                        {
                            var sender = await c;
                            sender.Send(m);
                        }
                        catch (Exception exception)
                        {
                            this.messageCenter.SendRejection(m, Message.RejectionTypes.Transient, $"Exception while sending message: {exception}");
                        }
                    }
                }
            }
        }

        public void Start()
        {
            stopped = false;
        }

        public void Stop()
        {
            stopped = true;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA1816:CallGCSuppressFinalizeCorrectly")]
        public void Dispose()
        {
            stopped = true;
            GC.SuppressFinalize(this);
        }
    }
}
