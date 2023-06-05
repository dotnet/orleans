#nullable enable

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime.Messaging
{
    internal sealed class SiloConnection : Connection
    {
        private static readonly Response PingResponse = Response.Completed;
        private readonly MessageCenter messageCenter;
        private readonly ConnectionManager connectionManager;
        private readonly ConnectionOptions connectionOptions;
        private readonly ProbeRequestMonitor probeMonitor;
        private readonly ConnectionPreambleHelper connectionPreambleHelper;

        public SiloConnection(
            SiloAddress remoteSiloAddress,
            ConnectionContext connection,
            ConnectionDelegate middleware,
            MessageCenter messageCenter,
            ILocalSiloDetails localSiloDetails,
            ConnectionManager connectionManager,
            ConnectionOptions connectionOptions,
            ConnectionCommon connectionShared,
            ProbeRequestMonitor probeMonitor,
            ConnectionPreambleHelper connectionPreambleHelper)
            : base(connection, middleware, connectionShared)
        {
            this.messageCenter = messageCenter;
            this.connectionManager = connectionManager;
            this.connectionOptions = connectionOptions;
            this.probeMonitor = probeMonitor;
            this.connectionPreambleHelper = connectionPreambleHelper;
            LocalSiloAddress = localSiloDetails.SiloAddress;
            LocalClusterId = localSiloDetails.ClusterId;
            RemoteSiloAddress = remoteSiloAddress;
        }

        public SiloAddress RemoteSiloAddress { get; private set; }

        public SiloAddress LocalSiloAddress { get; }

        public string LocalClusterId { get; }

        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.SiloToSilo;

        protected override IMessageCenter MessageCenter => messageCenter;

        protected override void RecordMessageReceive(Message msg, int numTotalBytes, int headerBytes)
        {
            MessagingInstruments.OnMessageReceive(msg, numTotalBytes, headerBytes, ConnectionDirection, RemoteSiloAddress);
        }

        protected override void RecordMessageSend(Message msg, int numTotalBytes, int headerBytes)
        {
            MessagingInstruments.OnMessageSend(msg, numTotalBytes, headerBytes, ConnectionDirection, RemoteSiloAddress);
        }

        protected override void OnReceivedMessage(Message msg)
        {
            // See it's a Ping message, and if so, short-circuit it
            if (msg.IsPing())
            {
                HandlePingMessage(msg);
                return;
            }

            // sniff message headers for directory cache management
            messageCenter.SniffIncomingMessage?.Invoke(msg);

            // Don't process messages that have already timed out
            if (msg.IsExpired)
            {
                MessagingTrace.OnDropExpiredMessage(msg, MessagingInstruments.Phase.Receive);
                return;
            }

            // If we've stopped application message processing, then filter those out now
            // Note that if we identify or add other grains that are required for proper stopping, we will need to treat them as we do the membership table grain here.
            if (messageCenter.IsBlockingApplicationMessages && !msg.IsSystemMessage)
            {
                // We reject new requests, and drop all other messages
                if (msg.Direction != Message.Directions.Request)
                {
                    MessagingTrace.OnDropBlockedApplicationMessage(msg);
                    return;
                }

                MessagingInstruments.OnRejectedMessage(msg);
                var rejection = MessageFactory.CreateRejectionResponse(msg, Message.RejectionTypes.Unrecoverable, "Silo stopping");
                Send(rejection);
                return;
            }

            // Make sure the message is for us. Note that some control messages may have no target
            // information, so a null target silo is OK.
            if (msg.TargetSilo == null || msg.TargetSilo.Matches(LocalSiloAddress))
            {
                messageCenter.ReceiveMessage(msg);
                return;
            }

            if (!msg.TargetSilo.Endpoint.Equals(LocalSiloAddress.Endpoint))
            {
                // If the message is for some other silo altogether, then we need to forward it.
                if (Log.IsEnabled(LogLevel.Trace)) Log.LogTrace("Forwarding message {Message} from {SendingSilo} to silo {TargetSilo}", msg.Id, msg.SendingSilo, msg.TargetSilo);
                messageCenter.SendMessage(msg);
                return;
            }

            // If the message was for this endpoint but an older epoch, then reject the message
            // (if it was a request), or drop it on the floor if it was a response or one-way.
            if (msg.Direction == Message.Directions.Request)
            {
                MessagingInstruments.OnRejectedMessage(msg);
                var rejection = MessageFactory.CreateRejectionResponse(
                    msg,
                    Message.RejectionTypes.Transient,
                    $"The target silo is no longer active: target was {msg.TargetSilo}, but this silo is {LocalSiloAddress}. The rejected message is {msg}.");

                // Invalidate the remote caller's activation cache entry.
                if (msg.TargetSilo != null)
                {
                    rejection.AddToCacheInvalidationHeader(new GrainAddress { GrainId = msg.TargetGrain, SiloAddress = msg.TargetSilo });
                }

                Send(rejection);

                if (Log.IsEnabled(LogLevel.Debug))
                {
                    Log.LogDebug(
                        "Rejecting an obsolete request; target was {TargetSilo}, but this silo is {SiloAddress}. The rejected message is {Message}.",
                        msg.TargetSilo?.ToString() ?? "null",
                        LocalSiloAddress.ToString(),
                        msg);
                }
            }
        }

        private void HandlePingMessage(Message msg)
        {
            MessagingInstruments.OnPingReceive(msg.SendingSilo);

            if (Log.IsEnabled(LogLevel.Trace))
            {
                var objectId = RuntimeHelpers.GetHashCode(msg);
                Log.LogTrace("Responding to Ping from {Silo} with object id {ObjectId}. Message {Message}", msg.SendingSilo, objectId, msg);
            }

            if (!msg.TargetSilo.Equals(LocalSiloAddress))
            {
                // Got ping that is not destined to me. For example, got a ping to my older incarnation.
                MessagingInstruments.OnRejectedMessage(msg);
                Message rejection = MessageFactory.CreateRejectionResponse(msg, Message.RejectionTypes.Unrecoverable,
                    $"The target silo is no longer active: target was {msg.TargetSilo}, but this silo is {LocalSiloAddress}. The rejected ping message is {msg}.");
                Send(rejection);
            }
            else
            {
                probeMonitor.OnReceivedProbeRequest();
                var response = MessageFactory.CreateResponseMessage(msg);
                response.BodyObject = PingResponse;
                Send(response);
            }
        }

        protected override void OnSendMessageFailure(Message message, string error)
        {
            if (message.IsPing())
            {
                Log.LogWarning("Failed to send ping message {Message}", message);
            }

            FailMessage(message, error);
        }

        protected override async Task RunInternal()
        {
            Exception? error = default;
            try
            {
                await Task.WhenAll(ReadPreamble(), WritePreamble());
                await base.RunInternal();
            }
            catch (Exception exception) when ((error = exception) is null)
            {
                Debug.Fail("Execution should not be able to reach this point.");
            }
            finally
            {
                if (!(RemoteSiloAddress is null))
                {
                    connectionManager.OnConnectionTerminated(RemoteSiloAddress, this, error);
                }
            }

            async Task WritePreamble()
            {
                await connectionPreambleHelper.Write(
                    Context,
                    new ConnectionPreamble
                    {
                        NodeIdentity = Constants.SiloDirectConnectionId,
                        NetworkProtocolVersion = connectionOptions.ProtocolVersion,
                        SiloAddress = LocalSiloAddress,
                        ClusterId = LocalClusterId
                    });
            }

            async Task ReadPreamble()
            {
                var preamble = await connectionPreambleHelper.Read(Context);

                if (!preamble.NodeIdentity.Equals(Constants.SiloDirectConnectionId))
                {
                    throw new InvalidOperationException("Unexpected client connection on silo endpoint.");
                }

                if (preamble.ClusterId != LocalClusterId)
                {
                    throw new InvalidOperationException($@"Unexpected cluster id ""{preamble.ClusterId}"", expected ""{LocalClusterId}""");
                }

                if (preamble.SiloAddress is not null)
                {
                    RemoteSiloAddress = preamble.SiloAddress;
                    connectionManager.OnConnected(preamble.SiloAddress, this);
                }
            }
        }

        protected override bool PrepareMessageForSend(Message msg)
        {
            // Don't send messages that have already timed out
            if (msg.IsExpired)
            {
                MessagingTrace.OnDropExpiredMessage(msg,  MessagingInstruments.Phase.Send);

                if (msg.IsPing())
                {
                    Log.LogWarning("Droppping expired ping message {Message}", msg);
                }

                return false;
            }

            // Fill in the outbound message with our silo address, if it's not already set
            msg.SendingSilo ??= LocalSiloAddress;

            if (Log.IsEnabled(LogLevel.Debug) && msg.IsPing())
            {
                Log.LogDebug("Sending ping message {Message}", msg);
            }

            if (RemoteSiloAddress is not null && msg.TargetSilo is not null && !RemoteSiloAddress.Matches(msg.TargetSilo))
            {
                Log.LogWarning(
                    "Attempting to send message addressed to {TargetSilo} to connection with {RemoteSiloAddress}. Message {Message}",
                    msg.TargetSilo,
                    RemoteSiloAddress,
                    msg);
            }

            return true;
        }

        public void FailMessage(Message msg, string reason)
        {
            if (msg.IsPing())
            {
                Log.LogWarning("Failed ping message {Message}", msg);
            }

            MessagingInstruments.OnFailedSentMessage(msg);
            if (msg.Direction == Message.Directions.Request)
            {
                if (Log.IsEnabled(LogLevel.Debug)) Log.LogDebug((int)ErrorCode.MessagingSendingRejection, "Silo {SiloAddress} is rejecting message: {Message}. Reason = {Reason}", LocalSiloAddress, msg, reason);

                // Done retrying, send back an error instead
                messageCenter.SendRejection(msg, Message.RejectionTypes.Transient, $"Silo {LocalSiloAddress} is rejecting message: {msg}. Reason = {reason}");
            }
            else
            {
                MessagingTrace.OnSiloDropSendingMessage(LocalSiloAddress, msg, reason);
            }
        }

        protected override void RetryMessage(Message msg, Exception? ex = null)
        {
            if (msg.IsPing())
            {
                Log.LogWarning("Retrying ping message {Message}", msg);
            }

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
    }
}
