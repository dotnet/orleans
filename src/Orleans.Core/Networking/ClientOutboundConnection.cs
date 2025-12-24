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
    internal sealed partial class ClientOutboundConnection(
        SiloAddress remoteSiloAddress,
        ConnectionContext connection,
        ConnectionDelegate middleware,
        ClientMessageCenter messageCenter,
        ConnectionManager connectionManager,
        ConnectionOptions connectionOptions,
        ConnectionCommon connectionShared,
        ConnectionPreambleHelper connectionPreambleHelper,
        ClusterOptions clusterOptions) : Connection(connection, middleware, connectionShared)
    {
        public SiloAddress RemoteSiloAddress { get; } = remoteSiloAddress ?? throw new ArgumentNullException(nameof(remoteSiloAddress));

        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.ClientToGateway;

        protected override IMessageCenter MessageCenter => messageCenter;

        protected override void RecordMessageReceive(Message msg, int numTotalBytes, int headerBytes)
        {
            MessagingInstruments.OnMessageReceive(msg, numTotalBytes, headerBytes, ConnectionDirection, RemoteSiloAddress);
        }

        protected override void RecordMessageSend(Message msg, int numTotalBytes, int headerBytes)
        {
            MessagingInstruments.OnMessageSend(msg, numTotalBytes, headerBytes, ConnectionDirection, RemoteSiloAddress);
        }

        protected override void OnReceivedMessage(Message message)
        {
            messageCenter.DispatchLocalMessage(message);
        }

        protected override async Task RunInternal()
        {
            Exception error = default;
            try
            {
                messageCenter.OnGatewayConnectionOpen();

                var myClusterId = clusterOptions.ClusterId;
                await connectionPreambleHelper.Write(
                    this.Context,
                    new ConnectionPreamble
                    {
                        NetworkProtocolVersion = connectionOptions.ProtocolVersion,
                        NodeIdentity = messageCenter.ClientId.GrainId,
                        SiloAddress = null,
                        ClusterId = myClusterId
                    });

                var preamble = await connectionPreambleHelper.Read(this.Context);
                LogInformationEstablishedConnection(this.Log, preamble.SiloAddress, preamble.NetworkProtocolVersion.ToString());

                if (preamble.ClusterId != myClusterId)
                {
                    throw new InvalidOperationException($@"Unexpected cluster id ""{preamble.ClusterId}"", expected ""{myClusterId}""");
                }

                await base.RunInternal();
            }
            catch (Exception exception) when ((error = exception) is null)
            {
                Debug.Fail("Execution should not be able to reach this point.");
            }
            finally
            {
                connectionManager.OnConnectionTerminated(this.RemoteSiloAddress, this, error);
                messageCenter.OnGatewayConnectionClosed();
            }
        }

        protected override bool PrepareMessageForSend(Message msg)
        {
            // Check to make sure we're not stopped
            if (!this.IsValid)
            {
                // Recycle the message we've dequeued. Note that this will recycle messages that were queued up to be sent when the gateway connection is declared dead
                msg.TargetSilo = null;
                messageCenter.SendMessage(msg);
                return false;
            }

            if (msg.TargetSilo != null) return true;

            msg.TargetSilo = this.RemoteSiloAddress;

            return true;
        }

        protected override void RetryMessage(Message msg, Exception ex = null)
        {
            if (msg == null) return;

            if (msg.RetryCount < MessagingOptions.DEFAULT_MAX_MESSAGE_SEND_RETRIES)
            {
                ++msg.RetryCount;
                messageCenter.SendMessage(msg);
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
            MessagingInstruments.OnRejectedMessage(msg);
            if (string.IsNullOrEmpty(reason)) reason = "Rejection from silo - Unknown reason.";
            var error = this.MessageFactory.CreateRejectionResponse(msg, rejectionType, reason);

            // rejection msgs are always originated locally, they are never remote.
            this.OnReceivedMessage(error);
        }

        public void FailMessage(Message msg, string reason)
        {
            MessagingInstruments.OnFailedSentMessage(msg);
            if (msg.Direction == Message.Directions.Request)
            {
                LogDebugClientIsRejectingMessage(this.Log, msg, reason);
                // Done retrying, send back an error instead
                this.SendRejection(msg, Message.RejectionTypes.Transient, $"Client is rejecting message: {msg}. Reason = {reason}");
            }
            else
            {
                LogInformationClientIsDroppingMessage(this.Log, msg, reason);
                MessagingInstruments.OnDroppedSentMessage(msg);
            }
        }

        protected override void OnSendMessageFailure(Message message, string error)
        {
            message.TargetSilo = null;
            messageCenter.SendMessage(message);
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Established connection to {Silo} with protocol version {ProtocolVersion}"
        )]
        private static partial void LogInformationEstablishedConnection(ILogger logger, SiloAddress silo, string protocolVersion);

        [LoggerMessage(
            Level = LogLevel.Debug,
            EventId = (int)ErrorCode.MessagingSendingRejection,
            Message = "Client is rejecting message: {Message}. Reason = {Reason}"
        )]
        private static partial void LogDebugClientIsRejectingMessage(ILogger logger, Message message, string reason);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.Messaging_OutgoingMS_DroppingMessage,
            Message = "Client is dropping message: {Message}. Reason = {Reason}"
        )]
        private static partial void LogInformationClientIsDroppingMessage(ILogger logger, Message message, string reason);
    }
}
