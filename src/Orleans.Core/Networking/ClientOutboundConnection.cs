using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Messaging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ClientOutboundConnection : Connection
    {
        private readonly ClientMessageCenter messageCenter;
        private readonly ConnectionManager connectionManager;
        private readonly ConnectionOptions connectionOptions;
        private readonly ConnectionPreambleHelper connectionPreambleHelper;
        private readonly SiloAddress remoteSiloAddress;

        public ClientOutboundConnection(
            SiloAddress remoteSiloAddress,
            ConnectionContext connection,
            ConnectionDelegate middleware,
            ClientMessageCenter messageCenter,
            ConnectionManager connectionManager,
            ConnectionOptions connectionOptions,
            ConnectionCommon connectionShared,
            ConnectionPreambleHelper connectionPreambleHelper)
            : base(connection, middleware, connectionShared)
        {
            this.messageCenter = messageCenter;
            this.connectionManager = connectionManager;
            this.connectionOptions = connectionOptions;
            this.connectionPreambleHelper = connectionPreambleHelper;
            this.remoteSiloAddress = remoteSiloAddress ?? throw new ArgumentNullException(nameof(remoteSiloAddress));
            this.MessageReceivedCounter = MessagingStatisticsGroup.GetMessageReceivedCounter(this.remoteSiloAddress);
            this.MessageSentCounter = MessagingStatisticsGroup.GetMessageSendCounter(this.remoteSiloAddress);
        }

        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.ClientToGateway;

        protected override IMessageCenter MessageCenter => this.messageCenter;

        protected override void OnReceivedMessage(Message message)
        {
            this.messageCenter.DispatchLocalMessage(message);
        }

        protected override async Task RunInternal()
        {
            Exception error = default;
            try
            {
                this.messageCenter.OnGatewayConnectionOpen();

                await connectionPreambleHelper.Write(
                    this.Context,
                    new ConnectionPreamble
                    {
                        NetworkProtocolVersion = this.connectionOptions.ProtocolVersion,
                        NodeIdentity = this.messageCenter.ClientId.GrainId,
                        SiloAddress = null,
                    });

                if (this.connectionOptions.ProtocolVersion >= NetworkProtocolVersion.Version2)
                {
                    var preamble = await connectionPreambleHelper.Read(this.Context);
                    this.Log.LogInformation(
                        "Established connection to {Silo} with protocol version {ProtocolVersion}",
                        preamble.SiloAddress,
                        preamble.NetworkProtocolVersion.ToString());
                }

                await base.RunInternal();
            }
            catch (Exception exception) when ((error = exception) is null)
            {
                Debug.Fail("Execution should not be able to reach this point.");
            }
            finally
            {
                this.connectionManager.OnConnectionTerminated(this.remoteSiloAddress, this, error);
                this.messageCenter.OnGatewayConnectionClosed();
            }
        }

        protected override bool PrepareMessageForSend(Message msg)
        {
            // Check to make sure we're not stopped
            if (!this.IsValid)
            {
                // Recycle the message we've dequeued. Note that this will recycle messages that were queued up to be sent when the gateway connection is declared dead
                msg.TargetActivation = default;
                msg.TargetSilo = null;
                this.messageCenter.SendMessage(msg);
                return false;
            }

            if (msg.TargetSilo != null) return true;

            msg.TargetSilo = this.remoteSiloAddress;
            if (msg.TargetGrain.IsSystemTarget())
                msg.TargetActivation = ActivationId.GetDeterministic(msg.TargetGrain);

            return true;
        }

        protected override void RetryMessage(Message msg, Exception ex = null)
        {
            if (msg == null) return;

            if (msg.RetryCount < MessagingOptions.DEFAULT_MAX_MESSAGE_SEND_RETRIES)
            {
                msg.RetryCount = msg.RetryCount + 1;
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

        internal void SendRejection(Message msg, Message.RejectionTypes rejectionType, string reason)
        {
            MessagingStatisticsGroup.OnRejectedMessage(msg);
            if (string.IsNullOrEmpty(reason)) reason = string.Format("Rejection from silo - Unknown reason.");
            var error = this.MessageFactory.CreateRejectionResponse(msg, rejectionType, reason);

            // rejection msgs are always originated locally, they are never remote.
            this.OnReceivedMessage(error);
        }

        public void FailMessage(Message msg, string reason)
        {
            MessagingStatisticsGroup.OnFailedSentMessage(msg);
            if (msg.Direction == Message.Directions.Request)
            {
                if (this.Log.IsEnabled(LogLevel.Debug)) this.Log.Debug(ErrorCode.MessagingSendingRejection, "Client is rejecting message: {Message}. Reason = {Reason}", msg, reason);
                // Done retrying, send back an error instead
                this.SendRejection(msg, Message.RejectionTypes.Transient, $"Client is rejecting message: {msg}. Reason = {reason}");
            }
            else
            {
                this.Log.Info(ErrorCode.Messaging_OutgoingMS_DroppingMessage, "Client is dropping message: {<essage}. Reason = {Reason}", msg, reason);
                MessagingStatisticsGroup.OnDroppedSentMessage(msg);
            }
        }

        protected override void OnSendMessageFailure(Message message, string error)
        {
            message.TargetSilo = null;
            this.messageCenter.SendMessage(message);
        }
    }
}
