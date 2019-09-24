using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
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
        private readonly MessageFactory messageFactory;
        private readonly ClientMessageCenter messageCenter;
        private readonly ConnectionManager connectionManager;
        private readonly ConnectionOptions connectionOptions;
        private readonly SiloAddress remoteSiloAddress;

        public ClientOutboundConnection(
            SiloAddress remoteSiloAddress,
            ConnectionContext connection,
            ConnectionDelegate middleware,
            MessageFactory messageFactory,
            IServiceProvider serviceProvider,
            ClientMessageCenter messageCenter,
            INetworkingTrace trace,
            ConnectionManager connectionManager,
            ConnectionOptions connectionOptions)
            : base(connection, middleware, serviceProvider, trace)
        {
            this.messageFactory = messageFactory;
            this.messageCenter = messageCenter;
            this.connectionManager = connectionManager;
            this.connectionOptions = connectionOptions;
            this.remoteSiloAddress = remoteSiloAddress ?? throw new ArgumentNullException(nameof(remoteSiloAddress));
            this.MessageReceivedCounter = MessagingStatisticsGroup.GetMessageReceivedCounter(this.remoteSiloAddress);
            this.MessageSentCounter = MessagingStatisticsGroup.GetMessageSendCounter(this.remoteSiloAddress);
        }

        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.ClientToGateway;

        protected override void OnReceivedMessage(Message message)
        {
            this.messageCenter.OnReceivedMessage(message);
        }

        protected override void OnReceiveMessageFailure(Message message, Exception exception)
        {
            // If deserialization completely failed or the message was one-way, rethrow the exception
            // so that it can be handled at another level.
            if (message?.Headers == null || message.Direction != Message.Directions.Request)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }

            // The message body was not successfully decoded, but the headers were.
            // Send a fast fail to the caller.
            MessagingStatisticsGroup.OnRejectedMessage(message);
            var response = this.messageFactory.CreateResponseMessage(message);
            response.Result = Message.ResponseTypes.Error;
            response.BodyObject = Response.ExceptionResponse(exception);

            // Send the error response and continue processing the next message.
            this.messageCenter.SendMessage(response);
        }

        protected override async Task RunInternal()
        {
            Exception error = default;
            try
            {
                this.messageCenter.OnGatewayConnectionOpen();

                await ConnectionPreamble.Write(
                    this.Context,
                    this.messageCenter.ClientId,
                    this.connectionOptions.ProtocolVersion,
                    siloAddress: null);

                if (this.connectionOptions.ProtocolVersion >= NetworkProtocolVersion.Version2)
                {
                    var (_, protocolVersion, siloAddress) = await ConnectionPreamble.Read(this.Context);
                    this.Log.LogInformation(
                        "Established connection to {Silo} with protocol version {ProtocolVersion}",
                        siloAddress,
                        protocolVersion.ToString());
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
                msg.TargetActivation = null;
                msg.TargetSilo = null;
                this.messageCenter.SendMessage(msg);
                return false;
            }

            if (msg.TargetSilo != null) return true;

            msg.TargetSilo = this.remoteSiloAddress;
            if (msg.TargetGrain.IsSystemTarget)
                msg.TargetActivation = ActivationId.GetSystemActivation(msg.TargetGrain, msg.TargetSilo);

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
            var error = this.messageFactory.CreateRejectionResponse(msg, rejectionType, reason);

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

        protected override void OnMessageSerializationFailure(Message msg, Exception exc)
        {
            // we only get here if we failed to serialize the msg (or any other catastrophic failure).
            // Request msg fails to serialize on the sender, so we just enqueue a rejection msg.
            // Response msg fails to serialize on the responding silo, so we try to send an error response back.
            this.Log.LogWarning(
                (int)ErrorCode.ProxyClient_SerializationError,
                "Unexpected error serializing message {Message}: {Exception}",
                msg,
                exc);

            MessagingStatisticsGroup.OnFailedSentMessage(msg);

            if (msg.Direction == Message.Directions.Request)
            {
                this.messageCenter.RejectMessage(msg, $"Unable to serialize message. Encountered exception: {exc?.GetType()}: {exc?.Message}", exc);
            }
            else if (msg.Direction == Message.Directions.Response && msg.RetryCount < MessagingOptions.DEFAULT_MAX_MESSAGE_SEND_RETRIES)
            {
                // if we failed sending an original response, turn the response body into an error and reply with it.
                // unless we have already tried sending the response multiple times.
                msg.Result = Message.ResponseTypes.Error;
                msg.BodyObject = Response.ExceptionResponse(exc);
                msg.RetryCount = msg.RetryCount + 1;
                this.messageCenter.SendMessage(msg);
            }
            else
            {
                this.Log.LogWarning(
                    (int)ErrorCode.ProxyClient_DroppingMsg,
                    "Gateway client is dropping message which failed during serialization: {Message}. Exception = {Exception}",
                    msg,
                    exc);

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
