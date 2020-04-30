using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Messaging
{
    internal class MessageCenter : IMessageCenter, IDisposable
    {
        public Gateway Gateway { get; set; }
        private readonly ILogger log;
        private Action<Message> rerouteHandler;
        internal Func<Message, bool> ShouldDrop;
        private HostedClient hostedClient;
        private Action<Message> sniffIncomingMessageHandler;

        internal OutboundMessageQueue OutboundQueue { get; set; }
        private readonly MessageFactory messageFactory;
        private readonly ILoggerFactory loggerFactory;
        private readonly ConnectionManager senderManager;
        private readonly MessagingTrace messagingTrace;
        private SiloMessagingOptions messagingOptions;
        private IncomingMessageHandler messageHandler;

        internal bool IsBlockingApplicationMessages { get; private set; }

        public void SetHostedClient(HostedClient client) => this.hostedClient = client;

        public bool IsProxying => this.Gateway != null || this.hostedClient?.ClientId != null;

        public bool TryDeliverToProxy(Message msg)
        {
            if (msg.TargetGrain is null || !msg.TargetGrain.IsClient) return false;
            if (this.Gateway is Gateway gateway && gateway.TryDeliverToProxy(msg)) return true;
            return this.hostedClient is HostedClient client && client.TryDispatchToClient(msg);
        }
        
        // This is determined by the IMA but needed by the OMS, and so is kept here in the message center itself.
        public SiloAddress MyAddress { get; private set; }

        public MessageCenter(
            ILocalSiloDetails siloDetails,
            IOptions<SiloMessagingOptions> messagingOptions,
            MessageFactory messageFactory,
            Factory<MessageCenter, Gateway> gatewayFactory,
            ILoggerFactory loggerFactory,
            IOptions<StatisticsOptions> statisticsOptions,
            ISiloStatusOracle siloStatusOracle,
            ConnectionManager senderManager,
            MessagingTrace messagingTrace)
        {
            this.messagingOptions = messagingOptions.Value;
            this.loggerFactory = loggerFactory;
            this.senderManager = senderManager;
            this.messagingTrace = messagingTrace;
            this.log = loggerFactory.CreateLogger<MessageCenter>();
            this.messageFactory = messageFactory;
            this.MyAddress = siloDetails.SiloAddress;

            if (log.IsEnabled(LogLevel.Trace)) log.Trace("Starting initialization.");

            OutboundQueue = new OutboundMessageQueue(this, this.loggerFactory.CreateLogger<OutboundMessageQueue>(), this.senderManager, siloStatusOracle, this.messagingTrace);

            if (log.IsEnabled(LogLevel.Trace)) log.Trace("Completed initialization.");

            if (siloDetails.GatewayAddress != null)
            {
                Gateway = gatewayFactory(this);
            }
        }

        public void Start()
        {
            IsBlockingApplicationMessages = false;
            OutboundQueue.Start();
        }

        public void StartGateway(ClientObserverRegistrar clientRegistrar)
        {
            if (Gateway != null)
                Gateway.Start(clientRegistrar);
        }

        private void WaitToRerouteAllQueuedMessages()
        {
            DateTime maxWaitTime = DateTime.UtcNow + this.messagingOptions.ShutdownRerouteTimeout;
            while (DateTime.UtcNow < maxWaitTime)
            {
                var applicationMessageQueueLength = this.OutboundQueue.GetApplicationMessageCount();
                if (applicationMessageQueueLength == 0)
                    break;
                Thread.Sleep(100);
            }
            
        }

        public void Stop()
        {
            IsBlockingApplicationMessages = true;
            
            StopAcceptingClientMessages();

            try
            {
                WaitToRerouteAllQueuedMessages();
                OutboundQueue.Stop();
            }
            catch (Exception exc)
            {
                log.Error(ErrorCode.Runtime_Error_100110, "Stop failed.", exc);
            }
        }

        public void StopAcceptingClientMessages()
        {
            if (log.IsEnabled(LogLevel.Debug)) log.Debug("StopClientMessages");
            if (Gateway == null) return;

            try
            {
                Gateway.Stop();
            }
            catch (Exception exc) { log.Error(ErrorCode.Runtime_Error_100109, "Stop failed.", exc); }
            Gateway = null;
        }

        public Action<Message> RerouteHandler
        {
            set
            {
                if (rerouteHandler != null)
                    throw new InvalidOperationException("MessageCenter RerouteHandler already set");
                rerouteHandler = value;
            }
        }

        public void OnReceivedMessage(Message message)
        {
            var handler = this.messageHandler;
            if (handler is null)
            {
                ThrowNullMessageHandler();
            }
            else
            {
                handler.ReceiveMessage(message);
            }

            static void ThrowNullMessageHandler() => throw new InvalidOperationException("MessageCenter does not have a message handler set");
        }

        public void RerouteMessage(Message message)
        {
            if (rerouteHandler != null)
                rerouteHandler(message);
            else
                SendMessage(message);
        }

        public Action<Message> SniffIncomingMessage
        {
            set
            {
                if (this.sniffIncomingMessageHandler != null)
                    throw new InvalidOperationException("IncomingMessageAcceptor SniffIncomingMessage already set");

                this.sniffIncomingMessageHandler = value;
            }

            get => this.sniffIncomingMessageHandler;
        }

        public void SendMessage(Message msg)
        {
            // Note that if we identify or add other grains that are required for proper stopping, we will need to treat them as we do the membership table grain here.
            if (IsBlockingApplicationMessages && (msg.Category == Message.Categories.Application) && (msg.Result != Message.ResponseTypes.Rejection)
                && !Constants.SystemMembershipTableId.Equals(msg.TargetGrain))
            {
                // Drop the message on the floor if it's an application message that isn't a rejection
                this.messagingTrace.OnDropBlockedApplicationMessage(msg);
            }
            else
            {
                msg.SendingSilo ??= this.MyAddress;
                OutboundQueue.SendMessage(msg);
            }
        }

        public bool TrySendLocal(Message message)
        {
            if (!message.TargetSilo.Matches(MyAddress))
            {
                return false;
            }

            if (log.IsEnabled(LogLevel.Trace)) log.Trace("Message has been looped back to this silo: {0}", message);
            MessagingStatisticsGroup.LocalMessagesSent.Increment();
            this.OnReceivedMessage(message);

            return true;
        }

        internal void SendRejection(Message msg, Message.RejectionTypes rejectionType, string reason)
        {
            MessagingStatisticsGroup.OnRejectedMessage(msg);
            if (string.IsNullOrEmpty(reason)) reason = string.Format("Rejection from silo {0} - Unknown reason.", MyAddress);
            Message error = this.messageFactory.CreateRejectionResponse(msg, rejectionType, reason);
            // rejection msgs are always originated in the local silo, they are never remote.
            this.OnReceivedMessage(error);
        }

        public void RegisterLocalMessageHandler(IncomingMessageHandler handler)
        {
            this.messageHandler = handler;
        }

        public void Dispose()
        {
            OutboundQueue?.Dispose();

            GC.SuppressFinalize(this);
        }

        public int SendQueueLength { get { return OutboundQueue.GetCount(); } }

        /// <summary>
        /// Indicates that application messages should be blocked from being sent or received.
        /// This method is used by the "fast stop" process.
        /// <para>
        /// Specifically, all outbound application messages are dropped, except for rejections and messages to the membership table grain.
        /// Inbound application requests are rejected, and other inbound application messages are dropped.
        /// </para>
        /// </summary>
        public void BlockApplicationMessages()
        {
            if(log.IsEnabled(LogLevel.Debug)) log.Debug("BlockApplicationMessages");
            IsBlockingApplicationMessages = true;
        }
    }
}
