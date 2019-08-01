using System;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Networking.Shared;

namespace Orleans.Runtime.Messaging
{
    internal sealed class SiloConnection : Connection
    {
        private readonly MessageCenter messageCenter;
        private readonly MessageFactory messageFactory;
        private readonly ConnectionManager connectionManager;
        private readonly ConnectionOptions connectionOptions;

        public SiloConnection(
            SiloAddress remoteSiloAddress,
            ConnectionContext connection,
            ConnectionDelegate middleware,
            IServiceProvider serviceProvider,
            INetworkingTrace trace,
            MessageCenter messageCenter,
            MessageFactory messageFactory,
            ILocalSiloDetails localSiloDetails,
            ConnectionManager connectionManager,
            ConnectionOptions connectionOptions)
            : base(connection, middleware, serviceProvider, trace)
        {
            this.messageCenter = messageCenter;
            this.messageFactory = messageFactory;
            this.connectionManager = connectionManager;
            this.connectionOptions = connectionOptions;
            this.LocalSiloAddress = localSiloDetails.SiloAddress;
            this.RemoteSiloAddress = remoteSiloAddress;
        }

        protected override IMessageCenter MessageCenter => this.messageCenter;

        public SiloAddress RemoteSiloAddress { get; private set; }

        public SiloAddress LocalSiloAddress { get; }

        protected override void OnReceivedMessage(Message msg)
        {
            // See it's a Ping message, and if so, short-circuit it
            var requestContext = msg.RequestContextData;
            if (requestContext != null &&
                requestContext.TryGetValue(RequestContext.PING_APPLICATION_HEADER, out var pingObj) &&
                pingObj is bool &&
                (bool)pingObj)
            {
                MessagingStatisticsGroup.OnPingReceive(msg.SendingSilo);

                if (this.Log.IsEnabled(LogLevel.Trace)) this.Log.Trace("Responding to Ping from {0}", msg.SendingSilo);

                if (!msg.TargetSilo.Equals(messageCenter.MyAddress)) // got ping that is not destined to me. For example, got a ping to my older incarnation.
                {
                    MessagingStatisticsGroup.OnRejectedMessage(msg);
                    Message rejection = this.messageFactory.CreateRejectionResponse(msg, Message.RejectionTypes.Unrecoverable,
                        $"The target silo is no longer active: target was {msg.TargetSilo.ToLongString()}, but this silo is {messageCenter.MyAddress.ToLongString()}. " +
                        $"The rejected ping message is {msg}.");
                    messageCenter.OutboundQueue.SendMessage(rejection);
                }
                else
                {
                    var response = this.messageFactory.CreateResponseMessage(msg);
                    response.BodyObject = Response.Done;
                    this.messageCenter.SendMessage(response);
                }
                return;
            }

            // sniff message headers for directory cache management
            this.messageCenter.SniffIncomingMessage?.Invoke(msg);

            // Don't process messages that have already timed out
            if (msg.IsExpired)
            {
                msg.DropExpiredMessage(MessagingStatisticsGroup.Phase.Receive);
                return;
            }

            // If we've stopped application message processing, then filter those out now
            // Note that if we identify or add other grains that are required for proper stopping, we will need to treat them as we do the membership table grain here.
            if (messageCenter.IsBlockingApplicationMessages && (msg.Category == Message.Categories.Application) && !Constants.SystemMembershipTableId.Equals(msg.SendingGrain))
            {
                // We reject new requests, and drop all other messages
                if (msg.Direction != Message.Directions.Request) return;

                MessagingStatisticsGroup.OnRejectedMessage(msg);
                var reject = this.messageFactory.CreateRejectionResponse(msg, Message.RejectionTypes.Unrecoverable, "Silo stopping");
                this.messageCenter.SendMessage(reject);
                return;
            }

            // Make sure the message is for us. Note that some control messages may have no target
            // information, so a null target silo is OK.
            if ((msg.TargetSilo == null) || msg.TargetSilo.Matches(messageCenter.MyAddress))
            {
                // See if it's a message for a client we're proxying.
                if (messageCenter.IsProxying && messageCenter.TryDeliverToProxy(msg)) return;

                // Nope, it's for us
                messageCenter.OnReceivedMessage(msg);
                return;
            }

            if (!msg.TargetSilo.Endpoint.Equals(messageCenter.MyAddress.Endpoint))
            {
                // If the message is for some other silo altogether, then we need to forward it.
                if (this.Log.IsEnabled(LogLevel.Trace)) this.Log.Trace("Forwarding message {0} from {1} to silo {2}", msg.Id, msg.SendingSilo, msg.TargetSilo);
                messageCenter.OutboundQueue.SendMessage(msg);
                return;
            }

            // If the message was for this endpoint but an older epoch, then reject the message
            // (if it was a request), or drop it on the floor if it was a response or one-way.
            if (msg.Direction == Message.Directions.Request)
            {
                MessagingStatisticsGroup.OnRejectedMessage(msg);
                Message rejection = this.messageFactory.CreateRejectionResponse(msg, Message.RejectionTypes.Transient,
                    string.Format("The target silo is no longer active: target was {0}, but this silo is {1}. The rejected message is {2}.",
                        msg.TargetSilo.ToLongString(), messageCenter.MyAddress.ToLongString(), msg));

                // Invalidate the remote caller's activation cache entry.
                if (msg.TargetAddress != null) rejection.AddToCacheInvalidationHeader(msg.TargetAddress);

                messageCenter.OutboundQueue.SendMessage(rejection);
                if (this.Log.IsEnabled(LogLevel.Debug)) this.Log.Debug("Rejecting an obsolete request; target was {0}, but this silo is {1}. The rejected message is {2}.",
                    msg.TargetSilo.ToLongString(), messageCenter.MyAddress.ToLongString(), msg);
            }
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

        protected override void OnSendMessageFailure(Message message, string error)
        {
            this.FailMessage(message, error);
        }

        protected override async Task RunInternal()
        {
            try
            {
                if (this.connectionOptions.ProtocolVersion == NetworkProtocolVersion.Version1)
                {
                    // This version of the protocol does not support symmetric preamble, so either send or receive preamble depending on
                    // Whether or not this is an inbound or outbound connection.
                    if (this.RemoteSiloAddress is null)
                    {
                        // Inbound connection
                        var protocolVersion = await ReadPreamble();

                        // To support graceful transition to higher protocol versions, send a preamble if the remote endpoint supports it.
                        if (protocolVersion >= NetworkProtocolVersion.Version2)
                        {
                            await WritePreamble();
                        }
                    }
                    else
                    {
                        // Outbound connection
                        await WritePreamble();
                    }
                }
                else
                {
                    // Later versions of the protocol send and receive preamble at both ends of the connection.
                    if (this.RemoteSiloAddress is null)
                    {
                        // Inbound connection
                        var protocolVersion = await ReadPreamble();

                        // To support graceful transition from lower protocol versions, only send a preamble if the remote endpoint supports it.
                        if (protocolVersion >= NetworkProtocolVersion.Version2)
                        {
                            await WritePreamble();
                        }
                    }
                    else
                    {
                        // Outbound connection
                        await Task.WhenAll(ReadPreamble().AsTask(), WritePreamble());
                    }
                }

                await base.RunInternal();
            }
            finally
            {
                if (!(this.RemoteSiloAddress is null)) this.connectionManager.OnConnectionTerminated(this.RemoteSiloAddress, this);
            }

            async Task WritePreamble()
            {
                await ConnectionPreamble.Write(
                    this.Context,
                    Constants.SiloDirectConnectionId,
                    this.connectionOptions.ProtocolVersion,
                    this.LocalSiloAddress);
            }

            async ValueTask<NetworkProtocolVersion> ReadPreamble()
            {
                var (grainId, protocolVersion, siloAddress) = await ConnectionPreamble.Read(this.Context);

                if (!grainId.Equals(Constants.SiloDirectConnectionId))
                {
                    throw new InvalidOperationException("Unexpected non-proxied connection on silo endpoint.");
                }

                if (siloAddress != null)
                {
                    this.RemoteSiloAddress = siloAddress;
                    this.connectionManager.OnConnected(siloAddress, this);
                }

                return protocolVersion;
            }
        }

        protected override bool PrepareMessageForSend(Message msg)
        {
            // Don't send messages that have already timed out
            if (msg.IsExpired)
            {
                msg.DropExpiredMessage(MessagingStatisticsGroup.Phase.Send);
                return false;
            }

            // Fill in the outbound message with our silo address, if it's not already set
            if (msg.SendingSilo == null)
                msg.SendingSilo = this.LocalSiloAddress;

            return true;
        }

        public void FailMessage(Message msg, string reason)
        {
            MessagingStatisticsGroup.OnFailedSentMessage(msg);
            if (msg.Direction == Message.Directions.Request)
            {
                if (this.Log.IsEnabled(LogLevel.Debug)) this.Log.Debug(ErrorCode.MessagingSendingRejection, "Silo {SiloAddress} is rejecting message: {Message}. Reason = {Reason}", this.LocalSiloAddress, msg, reason);

                // Done retrying, send back an error instead
                this.messageCenter.SendRejection(msg, Message.RejectionTypes.Transient, $"Silo {this.LocalSiloAddress} is rejecting message: {msg}. Reason = {reason}");
            }
            else
            {
                this.Log.Info(ErrorCode.Messaging_OutgoingMS_DroppingMessage, "Silo {SiloAddress} is dropping message: {Message}. Reason = {Reason}", this.LocalSiloAddress, msg, reason);
                MessagingStatisticsGroup.OnDroppedSentMessage(msg);
            }
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

        protected override void OnMessageSerializationFailure(Message msg, Exception exc)
        {
            // we only get here if we failed to serialize the msg (or any other catastrophic failure).
            // Request msg fails to serialize on the sending silo, so we just enqueue a rejection msg.
            // Response msg fails to serialize on the responding silo, so we try to send an error response back.
            this.Log.LogWarning(
                (int)ErrorCode.MessagingUnexpectedSendError,
                "Unexpected error serializing message {Message}: {Exception}",
                msg,
                exc);

            MessagingStatisticsGroup.OnFailedSentMessage(msg);

            if (msg.Direction == Message.Directions.Request)
            {
                this.messageCenter.SendRejection(msg, Message.RejectionTypes.Unrecoverable, exc.ToString());
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
                    (int)ErrorCode.Messaging_OutgoingMS_DroppingMessage,
                    "Silo {SiloAddress} is dropping message which failed during serialization: {Message}. Exception = {Exception}",
                    this.LocalSiloAddress,
                    msg,
                    exc);

                MessagingStatisticsGroup.OnDroppedSentMessage(msg);
            }
        }
    }
}
