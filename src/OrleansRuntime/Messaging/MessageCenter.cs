using System;
using System.Net;
using System.Threading;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.Messaging
{
    internal class MessageCenter : ISiloMessageCenter, IDisposable
    {
        private Gateway Gateway { get; set; }
        private IncomingMessageAcceptor ima;
        private static readonly Logger log = LogManager.GetLogger("Orleans.Messaging.MessageCenter");
        private Action<Message> rerouteHandler;
        internal Func<Message, bool> ShouldDrop;

        // ReSharper disable NotAccessedField.Local
        private IntValueStatistic sendQueueLengthCounter;
        private IntValueStatistic receiveQueueLengthCounter;
        // ReSharper restore NotAccessedField.Local

        internal IOutboundMessageQueue OutboundQueue { get; set; }
        internal IInboundMessageQueue InboundQueue { get; set; }
        internal SocketManager SocketManager;
        internal bool IsBlockingApplicationMessages { get; private set; }
        internal ISiloPerformanceMetrics Metrics { get; private set; }
        
        public bool IsProxying { get { return Gateway != null; } }

        public bool TryDeliverToProxy(Message msg)
        {
            return msg.TargetGrain.IsClient && Gateway != null && Gateway.TryDeliverToProxy(msg);
        }
        
        // This is determined by the IMA but needed by the OMS, and so is kept here in the message center itself.
        public SiloAddress MyAddress { get; private set; }

        public IMessagingConfiguration MessagingConfiguration { get; private set; }

        public MessageCenter(SiloInitializationParameters silo, NodeConfiguration nodeConfig, IMessagingConfiguration config, ISiloPerformanceMetrics metrics = null)
        {
            this.Initialize(silo.SiloAddress.Endpoint, nodeConfig.Generation, config, metrics);
            if (nodeConfig.IsGatewayNode)
            {
                this.InstallGateway(nodeConfig.ProxyGatewayEndpoint);
            }
        }

        private void Initialize(IPEndPoint here, int generation, IMessagingConfiguration config, ISiloPerformanceMetrics metrics = null)
        {
            if(log.IsVerbose3) log.Verbose3("Starting initialization.");

            SocketManager = new SocketManager(config);
            ima = new IncomingMessageAcceptor(this, here, SocketDirection.SiloToSilo);
            MyAddress = SiloAddress.New((IPEndPoint)ima.AcceptingSocket.LocalEndPoint, generation);
            MessagingConfiguration = config;
            InboundQueue = new InboundMessageQueue();
            OutboundQueue = new OutboundMessageQueue(this, config);
            Gateway = null;
            Metrics = metrics;
            
            sendQueueLengthCounter = IntValueStatistic.FindOrCreate(StatisticNames.MESSAGE_CENTER_SEND_QUEUE_LENGTH, () => SendQueueLength);
            receiveQueueLengthCounter = IntValueStatistic.FindOrCreate(StatisticNames.MESSAGE_CENTER_RECEIVE_QUEUE_LENGTH, () => ReceiveQueueLength);

            if (log.IsVerbose3) log.Verbose3("Completed initialization.");
        }

        public void InstallGateway(IPEndPoint gatewayAddress)
        {
            Gateway = new Gateway(this, gatewayAddress);
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
            if (log.IsVerbose) log.Verbose("StopClientMessages");
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

        internal void SendRejection(Message msg, Message.RejectionTypes rejectionType, string reason)
        {
            MessagingStatisticsGroup.OnRejectedMessage(msg);
            if (string.IsNullOrEmpty(reason)) reason = String.Format("Rejection from silo {0} - Unknown reason.", MyAddress);
            Message error = msg.CreateRejectionResponse(rejectionType, reason);
            // rejection msgs are always originated in the local silo, they are never remote.
            InboundQueue.PostMessage(error);
        }

        public Message WaitMessage(Message.Categories type, CancellationToken ct)
        {
            return InboundQueue.WaitMessage(type);
        }

        public void Dispose()
        {
            if (ima != null)
            {
                ima.Dispose();
                ima = null;
            }

            OutboundQueue.Dispose();

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
            if(log.IsVerbose) log.Verbose("BlockApplicationMessages");
            IsBlockingApplicationMessages = true;
        }
    }
}
