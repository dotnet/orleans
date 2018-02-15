using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Messaging;
using Orleans.Serialization;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Orleans.Runtime.Messaging
{
    internal class MessageCenter : ISiloMessageCenter, IDisposable
    {
        private Gateway Gateway { get; set; }
        private IncomingMessageAcceptor ima;
        private readonly ILogger log;
        private Action<Message> rerouteHandler;
        internal Func<Message, bool> ShouldDrop;

        // ReSharper disable NotAccessedField.Local
        private IntValueStatistic sendQueueLengthCounter;
        private IntValueStatistic receiveQueueLengthCounter;
        // ReSharper restore NotAccessedField.Local

        internal IOutboundMessageQueue OutboundQueue { get; set; }
        internal IInboundMessageQueue InboundQueue { get; set; }
        internal SocketManager SocketManager;
        private readonly SerializationManager serializationManager;
        private readonly MessageFactory messageFactory;
        private readonly ILoggerFactory loggerFactory;
        private readonly ExecutorService executorService;
        private readonly Action<Message>[] localMessageHandlers;

        internal bool IsBlockingApplicationMessages { get; private set; }
        
        public bool IsProxying { get { return Gateway != null; } }

        public bool TryDeliverToProxy(Message msg)
        {
            return msg.TargetGrain.IsClient && Gateway != null && Gateway.TryDeliverToProxy(msg);
        }
        
        // This is determined by the IMA but needed by the OMS, and so is kept here in the message center itself.
        public SiloAddress MyAddress { get; private set; }

        public MessageCenter(
            ILocalSiloDetails siloDetails,
            IOptions<EndpointOptions> endpointOptions,
            IOptions<SiloMessagingOptions> messagingOptions,
            IOptions<NetworkingOptions> networkingOptions,
            SerializationManager serializationManager,
            MessageFactory messageFactory,
            Factory<MessageCenter, Gateway> gatewayFactory,
            ExecutorService executorService,
            ILoggerFactory loggerFactory)
        {
            this.loggerFactory = loggerFactory;
            this.log = loggerFactory.CreateLogger<MessageCenter>();
            this.serializationManager = serializationManager;
            this.messageFactory = messageFactory;
            this.executorService = executorService;
            this.MyAddress = siloDetails.SiloAddress;
            this.Initialize(endpointOptions, messagingOptions, networkingOptions);
            if (siloDetails.GatewayAddress != null)
            {
                Gateway = gatewayFactory(this);
            }

            localMessageHandlers = new Action<Message>[Enum.GetValues(typeof(Message.Categories)).Length];
        }

        private void Initialize(IOptions<EndpointOptions> endpointOptions, IOptions<SiloMessagingOptions> messagingOptions, IOptions<NetworkingOptions> networkingOptions)
        {
            if(log.IsEnabled(LogLevel.Trace)) log.Trace("Starting initialization.");

            SocketManager = new SocketManager(networkingOptions, this.loggerFactory);
            var listeningEndpoint = endpointOptions.Value.GetListeningSiloEndpoint();
            ima = new IncomingMessageAcceptor(this, listeningEndpoint, SocketDirection.SiloToSilo, this.messageFactory, this.serializationManager, this.executorService, this.loggerFactory);
            InboundQueue = new InboundMessageQueue(this.loggerFactory);
            OutboundQueue = new OutboundMessageQueue(this, messagingOptions, this.serializationManager, this.executorService, this.loggerFactory);
            
            sendQueueLengthCounter = IntValueStatistic.FindOrCreate(StatisticNames.MESSAGE_CENTER_SEND_QUEUE_LENGTH, () => SendQueueLength);
            receiveQueueLengthCounter = IntValueStatistic.FindOrCreate(StatisticNames.MESSAGE_CENTER_RECEIVE_QUEUE_LENGTH, () => ReceiveQueueLength);

            if (log.IsEnabled(LogLevel.Trace)) log.Trace("Completed initialization.");
        }

        public void Start()
        {
            IsBlockingApplicationMessages = false;
            ima.Start();
            OutboundQueue.Start();
        }

        public void StartGateway(ClientObserverRegistrar clientRegistrar)
        {
            if (Gateway != null)
                Gateway.Start(clientRegistrar);
        }

        public void PrepareToStop()
        {
        }

        public void Stop()
        {
            IsBlockingApplicationMessages = true;

            try
            {
                ima.Stop();
            }
            catch (Exception exc)
            {
                log.Error(ErrorCode.Runtime_Error_100108, "Stop failed.", exc);
            }

            StopAcceptingClientMessages();

            try
            {
                OutboundQueue.Stop();
            }
            catch (Exception exc)
            {
                log.Error(ErrorCode.Runtime_Error_100110, "Stop failed.", exc);
            }

            try
            {
                SocketManager.Stop();
            }
            catch (Exception exc)
            {
                log.Error(ErrorCode.Runtime_Error_100111, "Stop failed.", exc);
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
                ima.SniffIncomingMessage = value;
            }
        }

        public Func<SiloAddress, bool> SiloDeadOracle { get; set; }

        public void SendMessage(Message msg)
        {
            // Note that if we identify or add other grains that are required for proper stopping, we will need to treat them as we do the membership table grain here.
            if (IsBlockingApplicationMessages && (msg.Category == Message.Categories.Application) && (msg.Result != Message.ResponseTypes.Rejection)
                && !Constants.SystemMembershipTableId.Equals(msg.TargetGrain))
            {
                // Drop the message on the floor if it's an application message that isn't a rejection
            }
            else
            {
                if (msg.SendingSilo == null)
                    msg.SendingSilo = MyAddress;
                OutboundQueue.SendMessage(msg);
            }
        }

        public bool TrySendLocal(Message message)
        {
            if (!message.TargetSilo.Equals(MyAddress))
            {
                return false;
            }

            if (log.IsEnabled(LogLevel.Trace)) log.Trace("Message has been looped back to this silo: {0}", message);
            MessagingStatisticsGroup.LocalMessagesSent.Increment();
            var localHandler = localMessageHandlers[(int) message.Category];
            if (localHandler != null)
            {
                localHandler(message);
            }
            else
            {
                InboundQueue.PostMessage(message);
            }

            return true;
        }

        internal void SendRejection(Message msg, Message.RejectionTypes rejectionType, string reason)
        {
            MessagingStatisticsGroup.OnRejectedMessage(msg);
            if (string.IsNullOrEmpty(reason)) reason = string.Format("Rejection from silo {0} - Unknown reason.", MyAddress);
            Message error = this.messageFactory.CreateRejectionResponse(msg, rejectionType, reason);
            // rejection msgs are always originated in the local silo, they are never remote.
            InboundQueue.PostMessage(error);
        }

        public Message WaitMessage(Message.Categories type, CancellationToken ct)
        {
            return InboundQueue.WaitMessage(type);
        }

        public void RegisterLocalMessageHandler(Message.Categories category, Action<Message> handler)
        {
            localMessageHandlers[(int) category] = handler;
        }

        public void Dispose()
        {
            if (ima != null)
            {
                ima.Dispose();
                ima = null;
            }

            InboundQueue?.Dispose();
            OutboundQueue?.Dispose();

            GC.SuppressFinalize(this);
        }

        public int SendQueueLength { get { return OutboundQueue.Count; } }

        public int ReceiveQueueLength { get { return InboundQueue.Count; } }

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
