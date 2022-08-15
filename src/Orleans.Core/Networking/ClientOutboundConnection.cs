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
        private readonly ClusterOptions clusterOptions;
        private readonly ConnectionPreambleHelper connectionPreambleHelper;

        public ClientOutboundConnection(
            SiloAddress remoteSiloAddress,
            ConnectionContext connection,
            ConnectionDelegate middleware,
            ClientMessageCenter messageCenter,
            ConnectionManager connectionManager,
            ConnectionOptions connectionOptions,
            ConnectionCommon connectionShared,
            ConnectionPreambleHelper connectionPreambleHelper,
            ClusterOptions clusterOptions)
            : base(connection, middleware, connectionShared)
        {
            this.messageCenter = messageCenter;
            this.connectionManager = connectionManager;
            this.connectionOptions = connectionOptions;
            this.connectionPreambleHelper = connectionPreambleHelper;
            this.clusterOptions = clusterOptions;
            this.RemoteSiloAddress = remoteSiloAddress ?? throw new ArgumentNullException(nameof(remoteSiloAddress));
        }

        public SiloAddress RemoteSiloAddress { get; }

        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.ClientToGateway;

        protected override IMessageCenter MessageCenter => this.messageCenter;

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
            this.messageCenter.DispatchLocalMessage(message);
        }

        protected override async Task RunInternal()
        {
            Exception error = default;
            try
            {
                this.messageCenter.OnGatewayConnectionOpen();

                var myClusterId = clusterOptions.ClusterId;
                await connectionPreambleHelper.Write(
                    this.Context,
                    new ConnectionPreamble
                    {
                        NetworkProtocolVersion = this.connectionOptions.ProtocolVersion,
                        NodeIdentity = this.messageCenter.ClientId.GrainId,
                        SiloAddress = null,
                        ClusterId = myClusterId
                    });

                var preamble = await connectionPreambleHelper.Read(this.Context);
                this.Log.LogInformation(
                    "Established connection to {Silo} with protocol version {ProtocolVersion}",
                    preamble.SiloAddress,
                    preamble.NetworkProtocolVersion.ToString());

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
                this.connectionManager.OnConnectionTerminated(this.RemoteSiloAddress, this, error);
                this.messageCenter.OnGatewayConnectionClosed();
            }
        }

        protected override bool PrepareMessageForSend(Message msg)
        {
            // Check to make sure we're not stopped
            if (!this.IsValid)
            {
                // Recycle the message we've dequeued. Note that this will recycle messages that were queued up to be sent when the gateway connection is declared dead
                msg.TargetSilo = null;
                this.messageCenter.SendMessage(msg);
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
                if (this.Log.IsEnabled(LogLevel.Debug)) this.Log.LogDebug((int)ErrorCode.MessagingSendingRejection, "Client is rejecting message: {Message}. Reason = {Reason}", msg, reason);
                // Done retrying, send back an error instead
                this.SendRejection(msg, Message.RejectionTypes.Transient, $"Client is rejecting message: {msg}. Reason = {reason}");
            }
            else
            {
                this.Log.LogInformation((int)ErrorCode.Messaging_OutgoingMS_DroppingMessage, "Client is dropping message: {Message}. Reason = {Reason}", msg, reason);
                MessagingInstruments.OnDroppedSentMessage(msg);
            }
        }

        protected override void OnSendMessageFailure(Message message, string error)
        {
            message.TargetSilo = null;
            this.messageCenter.SendMessage(message);
        }
    }
}
