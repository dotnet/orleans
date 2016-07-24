using System;
using System.Net;
using System.Net.Sockets;

using Orleans.Messaging;

namespace Orleans.Runtime.Messaging
{
    internal class GatewayAcceptor : IncomingMessageAcceptor
    {
        private readonly Gateway gateway;
        private readonly CounterStatistic loadSheddingCounter;
        private readonly CounterStatistic gatewayTrafficCounter;

        internal GatewayAcceptor(MessageCenter msgCtr, Gateway gateway, IPEndPoint gatewayAddress)
            : base(msgCtr, gatewayAddress, SocketDirection.GatewayToClient)
        {
            this.gateway = gateway;
            loadSheddingCounter = CounterStatistic.FindOrCreate(StatisticNames.GATEWAY_LOAD_SHEDDING);
            gatewayTrafficCounter = CounterStatistic.FindOrCreate(StatisticNames.GATEWAY_RECEIVED);
        }
        
        protected override bool RecordOpenedSocket(Socket sock)
        {
            ThreadTrackingStatistic.FirstClientConnectedStartTracking();
            GrainId client;
            if (!ReceiveSocketPreample(sock, true, out client)) return false;

            gateway.RecordOpenedSocket(sock, client);
            return true;
        }
  
        // Always called under a lock
        protected override void RecordClosedSocket(Socket sock)
        {
            TryRemoveClosedSocket(sock); // don't count this closed socket in IMA, we count it in Gateway.
            gateway.RecordClosedSocket(sock);
        }

        /// <summary>
        /// Handles an incoming (proxied) message by rerouting it immediately and unconditionally,
        /// after some header massaging.
        /// </summary>
        /// <param name="msg"></param>
        /// <param name="receivedOnSocket"></param>
        protected override void HandleMessage(Message msg, Socket receivedOnSocket)
        {
            // Don't process messages that have already timed out
            if (msg.IsExpired)
            {
                msg.DropExpiredMessage(MessagingStatisticsGroup.Phase.Receive);
                return;
            }

            gatewayTrafficCounter.Increment();

            // Are we overloaded?
            if ((MessageCenter.Metrics != null) && MessageCenter.Metrics.IsOverloaded)
            {
                MessagingStatisticsGroup.OnRejectedMessage(msg);
                Message rejection = msg.CreateRejectionResponse(Message.RejectionTypes.GatewayTooBusy, "Shedding load");
                MessageCenter.TryDeliverToProxy(rejection);
                if (Log.IsVerbose) Log.Verbose("Rejecting a request due to overloading: {0}", msg.ToString());
                loadSheddingCounter.Increment();
                return;
            }

            SiloAddress targetAddress = gateway.TryToReroute(msg);
            msg.SendingSilo = MessageCenter.MyAddress;

            if (targetAddress == null)
            {
                // reroute via Dispatcher
                msg.RemoveHeader(Message.Header.TARGET_SILO);
                msg.RemoveHeader(Message.Header.TARGET_ACTIVATION);

                if (msg.TargetGrain.IsSystemTarget)
                {
                    msg.TargetSilo = MessageCenter.MyAddress;
                    msg.TargetActivation = ActivationId.GetSystemActivation(msg.TargetGrain, MessageCenter.MyAddress);
                }

                MessagingStatisticsGroup.OnMessageReRoute(msg);
                MessageCenter.RerouteMessage(msg);
            }
            else
            {
                // send directly
                msg.TargetSilo = targetAddress;
                MessageCenter.SendMessage(msg);
            }
        }
    }
}
