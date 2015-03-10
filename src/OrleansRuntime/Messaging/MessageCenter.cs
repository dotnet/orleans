/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;

using Orleans.Runtime.Configuration;
using Orleans.Messaging;

namespace Orleans.Runtime.Messaging
{
    internal class MessageCenter : ISiloMessageCenter, IDisposable
    {
        private Gateway Gateway { get; set; }
        private IncomingMessageAcceptor ima;
        private static readonly TraceLogger log = TraceLogger.GetLogger("Orleans.Messaging.MessageCenter");
        private Action<Message> rerouteHandler;

        // ReSharper disable UnaccessedField.Local
        private IntValueStatistic sendQueueLengthCounter;
        private IntValueStatistic receiveQueueLengthCounter;
        // ReSharper restore UnaccessedField.Local
        
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

        public MessageCenter(IPEndPoint here, int generation, IMessagingConfiguration config, ISiloPerformanceMetrics metrics = null)
        {
            Initialize(here, generation, config, metrics);
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
                && (msg.TargetGrain != Constants.SystemMembershipTableId))
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
