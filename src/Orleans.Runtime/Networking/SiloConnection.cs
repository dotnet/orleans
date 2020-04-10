using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Messaging;

namespace Orleans.Runtime.Messaging
{
    internal sealed class SiloConnection : Connection
    {
        private static readonly Response PingResponse = new Response(null);
        private readonly MessageCenter messageCenter;
        private readonly ConnectionManager connectionManager;
        private readonly ConnectionOptions connectionOptions;

        public SiloConnection(
            SiloAddress remoteSiloAddress,
            ConnectionContext connection,
            ConnectionDelegate middleware,
            MessageCenter messageCenter,
            ILocalSiloDetails localSiloDetails,
            ConnectionManager connectionManager,
            ConnectionOptions connectionOptions,
            ConnectionCommon connectionShared)
            : base(connection, middleware, connectionShared)
        {
            this.messageCenter = messageCenter;
            this.connectionManager = connectionManager;
            this.connectionOptions = connectionOptions;
            this.LocalSiloAddress = localSiloDetails.SiloAddress;
            this.RemoteSiloAddress = remoteSiloAddress;
        }

        public SiloAddress RemoteSiloAddress { get; private set; }

        public SiloAddress LocalSiloAddress { get; }

        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.SiloToSilo;

        protected override IMessageCenter MessageCenter => this.messageCenter;

        public NetworkProtocolVersion RemoteProtocolVersion { get; private set; }

        protected override void OnReceivedMessage(Message msg)
        {
            // See it's a Ping message, and if so, short-circuit it
            if (msg.IsPing())
            {
                this.HandlePingMessage(msg);
                return;
            }

            // sniff message headers for directory cache management
            this.messageCenter.SniffIncomingMessage?.Invoke(msg);

            // Don't process messages that have already timed out
            if (msg.IsExpired)
            {
                this.MessagingTrace.OnDropExpiredMessage(msg, MessagingStatisticsGroup.Phase.Receive);
                return;
            }

            // If we've stopped application message processing, then filter those out now
            // Note that if we identify or add other grains that are required for proper stopping, we will need to treat them as we do the membership table grain here.
            if (messageCenter.IsBlockingApplicationMessages && (msg.Category == Message.Categories.Application) && !Constants.SystemMembershipTableId.Equals(msg.SendingGrain))
            {
                // We reject new requests, and drop all other messages
                if (msg.Direction != Message.Directions.Request)
                {
                    this.MessagingTrace.OnDropBlockedApplicationMessage(msg);
                    return;
                }

                MessagingStatisticsGroup.OnRejectedMessage(msg);
                var rejection = this.MessageFactory.CreateRejectionResponse(msg, Message.RejectionTypes.Unrecoverable, "Silo stopping");
                this.Send(rejection);
                return;
            }

            // Make sure the message is for us. Note that some control messages may have no target
            // information, so a null target silo is OK.
            if ((msg.TargetSilo == null) || msg.TargetSilo.Matches(this.LocalSiloAddress))
            {
                // See if it's a message for a client we're proxying.
                if (messageCenter.TryDeliverToProxy(msg))
                {
                    return;
                }

                // Nope, it's for us
                messageCenter.OnReceivedMessage(msg);
                return;
            }

            if (!msg.TargetSilo.Endpoint.Equals(this.LocalSiloAddress.Endpoint))
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
                var rejection = this.MessageFactory.CreateRejectionResponse(
                    msg,
                    Message.RejectionTypes.Transient,
                    $"The target silo is no longer active: target was {msg.TargetSilo.ToLongString()}, but this silo is {this.LocalSiloAddress.ToLongString()}. The rejected message is {msg}.");

                // Invalidate the remote caller's activation cache entry.
                if (msg.TargetAddress != null)
                {
                    rejection.AddToCacheInvalidationHeader(msg.TargetAddress);
                }

                this.Send(rejection);

                if (this.Log.IsEnabled(LogLevel.Debug))
                {
                    this.Log.Debug(
                        "Rejecting an obsolete request; target was {0}, but this silo is {1}. The rejected message is {2}.",
                        msg.TargetSilo.ToLongString(),
                        this.LocalSiloAddress.ToLongString(),
                        msg);
                }
            }
        }

        private void HandlePingMessage(Message msg)
        {
            MessagingStatisticsGroup.OnPingReceive(msg.SendingSilo);

            if (this.Log.IsEnabled(LogLevel.Trace))
            {
                var objectId = RuntimeHelpers.GetHashCode(msg);
                this.Log.LogTrace("Responding to Ping from {Silo} with object id {ObjectId}. Message {Message}", msg.SendingSilo, objectId, msg);
            }

            if (!msg.TargetSilo.Equals(this.LocalSiloAddress))
            {
                // Got ping that is not destined to me. For example, got a ping to my older incarnation.
                MessagingStatisticsGroup.OnRejectedMessage(msg);
                Message rejection = this.MessageFactory.CreateRejectionResponse(msg, Message.RejectionTypes.Unrecoverable,
                    $"The target silo is no longer active: target was {msg.TargetSilo.ToLongString()}, but this silo is {this.LocalSiloAddress.ToLongString()}. " +
                    $"The rejected ping message is {msg}.");
                this.Send(rejection);
            }
            else
            {
                var response = this.MessageFactory.CreateResponseMessage(msg);
                response.BodyObject = PingResponse;
                this.Send(response);
            }
        }

        protected override void OnSendMessageFailure(Message message, string error)
        {
            if (message?.Headers != null && message.IsPing())
            {
                this.Log.LogWarning("Failed to send ping message {Message}", message);
            }

            this.FailMessage(message, error);
        }

        protected override async Task RunInternal()
        {
            Exception error = default;
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

                this.MessageReceivedCounter = MessagingStatisticsGroup.GetMessageReceivedCounter(this.RemoteSiloAddress);
                this.MessageSentCounter = MessagingStatisticsGroup.GetMessageSendCounter(this.RemoteSiloAddress);
                await base.RunInternal();
            }
            catch (Exception exception) when ((error = exception) is null)
            {
                Debug.Fail("Execution should not be able to reach this point.");
            }
            finally
            {
                if (!(this.RemoteSiloAddress is null))
                {
                    this.connectionManager.OnConnectionTerminated(this.RemoteSiloAddress, this, error);
                }
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

                if (siloAddress is object)
                {
                    this.RemoteSiloAddress = siloAddress;
                    this.connectionManager.OnConnected(siloAddress, this);
                }

                this.RemoteProtocolVersion = protocolVersion;

                return protocolVersion;
            }
        }

        protected override bool PrepareMessageForSend(Message msg)
        {
            // Don't send messages that have already timed out
            if (msg.IsExpired)
            {
                this.MessagingTrace.OnDropExpiredMessage(msg,  MessagingStatisticsGroup.Phase.Send);

                if (msg.IsPing())
                {
                    this.Log.LogWarning("Droppping expired ping message {Message}", msg);
                }

                return false;
            }

            // Fill in the outbound message with our silo address, if it's not already set
            msg.SendingSilo ??= this.LocalSiloAddress;

            if (this.Log.IsEnabled(LogLevel.Debug) && msg.IsPing())
            {
                this.Log.LogDebug("Sending ping message {Message}", msg);
            }

            if (this.RemoteSiloAddress is object && msg.TargetSilo is object && !this.RemoteSiloAddress.Matches(msg.TargetSilo))
            {
                this.Log.LogWarning(
                    "Attempting to send message addressed to {TargetSilo} to connection with {RemoteSiloAddress}. Message {Message}",
                    msg.TargetSilo,
                    this.RemoteSiloAddress,
                    msg);
            }

            return true;
        }

        public void FailMessage(Message msg, string reason)
        {
            if (msg?.Headers != null && msg.IsPing())
            {
                this.Log.LogWarning("Failed ping message {Message}", msg);
            }

            MessagingStatisticsGroup.OnFailedSentMessage(msg);
            if (msg.Direction == Message.Directions.Request)
            {
                if (this.Log.IsEnabled(LogLevel.Debug)) this.Log.LogDebug((int)ErrorCode.MessagingSendingRejection, "Silo {SiloAddress} is rejecting message: {Message}. Reason = {Reason}", this.LocalSiloAddress, msg, reason);

                // Done retrying, send back an error instead
                this.messageCenter.SendRejection(msg, Message.RejectionTypes.Transient, $"Silo {this.LocalSiloAddress} is rejecting message: {msg}. Reason = {reason}");
            }
            else
            {
                this.MessagingTrace.OnSiloDropSendingMessage(this.LocalSiloAddress, msg, reason);
            }
        }

        public override void Send(Message message)
        {
            if (this.RemoteProtocolVersion == NetworkProtocolVersion.Version1 && this.RemoteSiloAddress is null)
            {
                // Incoming Version1 connections are half-duplex (read-only)
                this.messageCenter.SendMessage(message);
            }
            else
            {
                base.Send(message);
            }
        }

        protected override void RetryMessage(Message msg, Exception ex = null)
        {
            if (msg == null) return;

            if (msg?.Headers != null && msg.IsPing())
            {
                this.Log.LogWarning("Retrying ping message {Message}", msg);
            }

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
    }
}
