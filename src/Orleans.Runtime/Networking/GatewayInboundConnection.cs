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
        private readonly SiloAddress myAddress;
        private readonly string myClusterId;

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
            this.myAddress = siloDetails.SiloAddress;
            this.myClusterId = siloDetails.ClusterId;
        }

        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.GatewayToClient;

        protected override IMessageCenter MessageCenter => this.messageCenter;

        protected override void RecordMessageReceive(Message msg, int numTotalBytes, int headerBytes)
        {
            MessagingInstruments.OnMessageReceive(msg, numTotalBytes, headerBytes, ConnectionDirection);
            GatewayInstruments.GatewayReceived.Add(1);
        }

        protected override void RecordMessageSend(Message msg, int numTotalBytes, int headerBytes)
        {
            MessagingInstruments.OnMessageSend(msg, numTotalBytes, headerBytes, ConnectionDirection);
            GatewayInstruments.GatewaySent.Add(1);
        }

        protected override void OnReceivedMessage(Message msg)
        {
            // Don't process messages that have already timed out
            if (msg.IsExpired)
            {
                this.MessagingTrace.OnDropExpiredMessage(msg, MessagingInstruments.Phase.Receive);
                return;
            }

            // Are we overloaded?
            if (this.overloadDetector.Overloaded)
            {
                MessagingInstruments.OnRejectedMessage(msg);
                Message rejection = this.MessageFactory.CreateRejectionResponse(msg, Message.RejectionTypes.GatewayTooBusy, "Shedding load");
                this.messageCenter.TryDeliverToProxy(rejection);
                if (this.Log.IsEnabled(LogLevel.Debug)) this.Log.LogDebug("Rejecting a request due to overloading: {Message}", msg.ToString());
                GatewayInstruments.GatewayLoadShedding.Add(1);
                return;
            }

            SiloAddress targetAddress = this.gateway.TryToReroute(msg);
            msg.SendingSilo = this.myAddress;
            if (targetAddress is null)
            {
                // reroute via Dispatcher
                msg.TargetSilo = null;

                if (SystemTargetGrainId.TryParse(msg.TargetGrain, out var systemTargetId))
                {
                    msg.TargetSilo = this.myAddress;
                    msg.TargetGrain = systemTargetId.WithSiloAddress(this.myAddress).GrainId;
                }

                MessagingInstruments.OnMessageReRoute(msg);
                this.messageCenter.RerouteMessage(msg);
            }
            else
            {
                // send directly
                msg.TargetSilo = targetAddress;

                if (SystemTargetGrainId.TryParse(msg.TargetGrain, out var systemTargetId))
                {
                    msg.TargetGrain = systemTargetId.WithSiloAddress(targetAddress).GrainId;
                }

                this.messageCenter.SendMessage(msg);
            }
        }

        protected override async Task RunInternal()
        {
            var preamble = await connectionPreambleHelper.Read(this.Context);

            await connectionPreambleHelper.Write(
                this.Context,
                new ConnectionPreamble
                {
                    NodeIdentity = Constants.SiloDirectConnectionId,
                    NetworkProtocolVersion = this.connectionOptions.ProtocolVersion,
                    SiloAddress = this.myAddress,
                    ClusterId = this.myClusterId
                });

            if (!ClientGrainId.TryParse(preamble.NodeIdentity, out var clientId))
            {
                throw new InvalidOperationException($"Unexpected connection id {preamble.NodeIdentity} on proxy endpoint from {preamble.SiloAddress?.ToString() ?? "unknown silo"}");
            }

            if (preamble.ClusterId != this.myClusterId)
            {
                throw new InvalidOperationException($@"Unexpected cluster id ""{preamble.ClusterId}"", expected ""{this.myClusterId}""");
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
                this.MessagingTrace.OnDropExpiredMessage(msg, MessagingInstruments.Phase.Send);
                return false;
            }

            // Fill in the outbound message with our silo address, if it's not already set
            msg.SendingSilo ??= this.myAddress;

            return true;
        }

        public void FailMessage(Message msg, string reason)
        {
            MessagingInstruments.OnFailedSentMessage(msg);
            if (msg.Direction == Message.Directions.Request)
            {
                if (this.Log.IsEnabled(LogLevel.Debug))
                    this.Log.LogDebug(
                        (int)ErrorCode.MessagingSendingRejection,
                        "Silo {SiloAddress} is rejecting message: {Message}. Reason = {Reason}",
                        this.myAddress,
                        msg,
                        reason);

                // Done retrying, send back an error instead
                this.messageCenter.SendRejection(
                    msg,
                    Message.RejectionTypes.Transient,
                    $"Silo {this.myAddress} is rejecting message: {msg}. Reason = {reason}");
            }
            else
            {
                this.Log.LogInformation(
                    (int)ErrorCode.Messaging_OutgoingMS_DroppingMessage,
                    "Silo {SiloAddress} is dropping message: {Message}. Reason = {Reason}",
                    this.myAddress,
                    msg,
                    reason);
                MessagingInstruments.OnDroppedSentMessage(msg);
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
