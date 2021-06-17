using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Messaging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class GatewayInboundConnection : Connection
    {
        private readonly MessageCenter messageCenter;
        private readonly ConnectionPreambleHelper connectionPreambleHelper;
        private readonly ConnectionOptions connectionOptions;
        private readonly Gateway gateway;
        private readonly OverloadDetector overloadDetector;
        private readonly CounterStatistic loadSheddingCounter;
        private readonly SiloAddress myAddress;

        public GatewayInboundConnection(
            ConnectionContext connection,
            ConnectionDelegate middleware,
            Gateway gateway,
            OverloadDetector overloadDetector,
            ILocalSiloDetails siloDetails,
            ConnectionOptions connectionOptions,
            MessageCenter messageCenter,
            ConnectionCommon connectionShared,
            ConnectionPreambleHelper connectionPreambleHelper)
            : base(connection, middleware, connectionShared)
        {
            this.connectionOptions = connectionOptions;
            this.gateway = gateway;
            this.overloadDetector = overloadDetector;
            this.messageCenter = messageCenter;
            this.connectionPreambleHelper = connectionPreambleHelper;
            this.loadSheddingCounter = CounterStatistic.FindOrCreate(StatisticNames.GATEWAY_LOAD_SHEDDING);
            this.myAddress = siloDetails.SiloAddress;
            this.MessageReceivedCounter = CounterStatistic.FindOrCreate(StatisticNames.GATEWAY_RECEIVED);
            this.MessageSentCounter = CounterStatistic.FindOrCreate(StatisticNames.GATEWAY_SENT);
        }

        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.GatewayToClient;

        protected override IMessageCenter MessageCenter => this.messageCenter;

        protected override void OnReceivedMessage(Message msg)
        {
            // Don't process messages that have already timed out
            if (msg.IsExpired)
            {
                this.MessagingTrace.OnDropExpiredMessage(msg, MessagingStatisticsGroup.Phase.Receive);
                return;
            }

            // Are we overloaded?
            if (this.overloadDetector.Overloaded)
            {
                MessagingStatisticsGroup.OnRejectedMessage(msg);
                Message rejection = this.MessageFactory.CreateRejectionResponse(msg, Message.RejectionTypes.GatewayTooBusy, "Shedding load");
                this.messageCenter.TryDeliverToProxy(rejection);
                if (this.Log.IsEnabled(LogLevel.Debug)) this.Log.Debug("Rejecting a request due to overloading: {0}", msg.ToString());
                loadSheddingCounter.Increment();
                return;
            }

            SiloAddress targetAddress = this.gateway.TryToReroute(msg);
            msg.SendingSilo = this.myAddress;
            if (targetAddress is null)
            {
                // reroute via Dispatcher
                msg.TargetSilo = null;
                msg.TargetActivation = default;
                msg.ClearTargetAddress();

                if (SystemTargetGrainId.TryParse(msg.TargetGrain, out var systemTargetId))
                {
                    msg.TargetSilo = this.myAddress;
                    msg.TargetGrain = systemTargetId.WithSiloAddress(this.myAddress).GrainId;
                    msg.TargetActivation = ActivationId.GetDeterministic(msg.TargetGrain);
                }

                MessagingStatisticsGroup.OnMessageReRoute(msg);
                this.messageCenter.RerouteMessage(msg);
            }
            else
            {
                // send directly
                msg.TargetSilo = targetAddress;

                if (SystemTargetGrainId.TryParse(msg.TargetGrain, out var systemTargetId))
                {
                    msg.TargetGrain = systemTargetId.WithSiloAddress(targetAddress).GrainId;
                    msg.TargetActivation = ActivationId.GetDeterministic(msg.TargetGrain);
                }

                this.messageCenter.SendMessage(msg);
            }
        }

        protected override async Task RunInternal()
        {
            var preamble = await connectionPreambleHelper.Read(this.Context);

            if (preamble.NetworkProtocolVersion >= NetworkProtocolVersion.Version3)
            {
                await connectionPreambleHelper.Write(
                    this.Context,
                    new ConnectionPreamble
                    {
                        NodeIdentity = Constants.SiloDirectConnectionId,
                        NetworkProtocolVersion = this.connectionOptions.ProtocolVersion,
                        SiloAddress = this.myAddress
                    });
            }

            if (!ClientGrainId.TryParse(preamble.NodeIdentity, out var clientId))
            {
                throw new InvalidOperationException($"Unexpected connection id {preamble.NodeIdentity} on proxy endpoint from {preamble.SiloAddress?.ToString() ?? "unknown silo"}");
            }

            try
            {
                this.gateway.RecordOpenedConnection(this, clientId);
                await base.RunInternal();
            }
            finally
            {
                this.gateway.RecordClosedConnection(this);
            }
        }

        protected override bool PrepareMessageForSend(Message msg)
        {
            // Don't send messages that have already timed out
            if (msg.IsExpired)
            {
                this.MessagingTrace.OnDropExpiredMessage(msg, MessagingStatisticsGroup.Phase.Send);
                return false;
            }

            // Fill in the outbound message with our silo address, if it's not already set
            msg.SendingSilo ??= this.myAddress;

            return true;
        }

        public void FailMessage(Message msg, string reason)
        {
            MessagingStatisticsGroup.OnFailedSentMessage(msg);
            if (msg.Direction == Message.Directions.Request)
            {
                if (this.Log.IsEnabled(LogLevel.Debug)) this.Log.Debug(ErrorCode.MessagingSendingRejection, "Silo {siloAddress} is rejecting message: {message}. Reason = {reason}", this.myAddress, msg, reason);

                // Done retrying, send back an error instead
                this.messageCenter.SendRejection(msg, Message.RejectionTypes.Transient, String.Format("Silo {0} is rejecting message: {1}. Reason = {2}", this.myAddress, msg, reason));
            }
            else
            {
                this.Log.Info(ErrorCode.Messaging_OutgoingMS_DroppingMessage, "Silo {siloAddress} is dropping message: {message}. Reason = {reason}", this.myAddress, msg, reason);
                MessagingStatisticsGroup.OnDroppedSentMessage(msg);
            }
        }

        protected override void RetryMessage(Message msg, Exception ex = null)
        {
            if (msg == null) return;

            if (msg.RetryCount < MessagingOptions.DEFAULT_MAX_MESSAGE_SEND_RETRIES)
            {
                msg.RetryCount++;
                this.messageCenter.SendMessage(msg);
            }
            else
            {
                var reason = new StringBuilder("Retry count exceeded. ");
                if (ex != null)
                {
                    reason.Append("Original exception is: ").Append(ex.ToString());
                }

                reason.Append("Msg is: ").Append(msg);
                FailMessage(msg, reason.ToString());
            }
        }

        protected override void OnSendMessageFailure(Message message, string error)
        {
            this.FailMessage(message, error);
        }
    }
}
