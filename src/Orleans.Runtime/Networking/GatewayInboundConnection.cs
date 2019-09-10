using System;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Messaging
{
    internal sealed class GatewayInboundConnection : Connection
    {
        private readonly MessageCenter messageCenter;
        private readonly ILocalSiloDetails siloDetails;
        private readonly MultiClusterOptions multiClusterOptions;
        private readonly ConnectionOptions connectionOptions;
        private readonly Gateway gateway;
        private readonly OverloadDetector overloadDetector;
        private readonly MessageFactory messageFactory;
        private readonly CounterStatistic loadSheddingCounter;
        private readonly CounterStatistic gatewayTrafficCounter;
        private readonly SiloAddress myAddress;

        public GatewayInboundConnection(
            ConnectionContext connection,
            ConnectionDelegate middleware,
            IServiceProvider serviceProvider,
            Gateway gateway,
            OverloadDetector overloadDetector,
            MessageFactory messageFactory,
            INetworkingTrace trace,
            ILocalSiloDetails siloDetails,
            IOptions<MultiClusterOptions> multiClusterOptions,
            ConnectionOptions connectionOptions,
            MessageCenter messageCenter,
            ILocalSiloDetails localSiloDetails)
            : base(connection, middleware, serviceProvider, trace)
        {
            this.connectionOptions = connectionOptions;
            this.gateway = gateway;
            this.overloadDetector = overloadDetector;
            this.messageFactory = messageFactory;
            this.siloDetails = siloDetails;
            this.messageCenter = messageCenter;
            this.multiClusterOptions = multiClusterOptions.Value;
            this.loadSheddingCounter = CounterStatistic.FindOrCreate(StatisticNames.GATEWAY_LOAD_SHEDDING);
            this.gatewayTrafficCounter = CounterStatistic.FindOrCreate(StatisticNames.GATEWAY_RECEIVED);
            this.myAddress = localSiloDetails.SiloAddress;
        }

        protected override IMessageCenter MessageCenter => this.messageCenter;

        protected override void OnReceivedMessage(Message msg)
        {
            // Don't process messages that have already timed out
            if (msg.IsExpired)
            {
                msg.DropExpiredMessage(MessagingStatisticsGroup.Phase.Receive);
                return;
            }

            gatewayTrafficCounter.Increment();

            // return address translation for geo clients (replace sending address cli/* with gcl/*)
            if (this.multiClusterOptions.HasMultiClusterNetwork && msg.SendingAddress.Grain.Category != UniqueKey.Category.GeoClient)
            {
                msg.SendingGrain = GrainId.NewClientId(msg.SendingAddress.Grain.PrimaryKey, this.siloDetails.ClusterId);
            }

            // Are we overloaded?
            if (this.overloadDetector.Overloaded)
            {
                MessagingStatisticsGroup.OnRejectedMessage(msg);
                Message rejection = this.messageFactory.CreateRejectionResponse(msg, Message.RejectionTypes.GatewayTooBusy, "Shedding load");
                this.messageCenter.TryDeliverToProxy(rejection);
                if (this.Log.IsEnabled(LogLevel.Debug)) this.Log.Debug("Rejecting a request due to overloading: {0}", msg.ToString());
                loadSheddingCounter.Increment();
                return;
            }

            SiloAddress targetAddress = this.gateway.TryToReroute(msg);
            msg.SendingSilo = this.messageCenter.MyAddress;

            if (targetAddress == null)
            {
                // reroute via Dispatcher
                msg.TargetSilo = null;
                msg.TargetActivation = null;
                msg.ClearTargetAddress();

                if (msg.TargetGrain.IsSystemTarget)
                {
                    msg.TargetSilo = this.messageCenter.MyAddress;
                    msg.TargetActivation = ActivationId.GetSystemActivation(msg.TargetGrain, this.messageCenter.MyAddress);
                }

                MessagingStatisticsGroup.OnMessageReRoute(msg);
                this.messageCenter.RerouteMessage(msg);
            }
            else
            {
                // send directly
                msg.TargetSilo = targetAddress;
                this.messageCenter.SendMessage(msg);
            }
        }

        protected override void OnReceiveMessageFailure(Message msg, Exception exception)
        {
            // If deserialization completely failed or the message was one-way, rethrow the exception
            // so that it can be handled at another level.
            if (msg?.Headers == null || msg.Direction != Message.Directions.Request)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }

            // The message body was not successfully decoded, but the headers were.
            // Send a fast fail to the caller.
            MessagingStatisticsGroup.OnRejectedMessage(msg);
            var response = this.messageFactory.CreateResponseMessage(msg);
            response.Result = Message.ResponseTypes.Error;
            response.BodyObject = Response.ExceptionResponse(exception);

            // Send the error response and continue processing the next message.
            this.messageCenter.SendMessage(response);
        }

        protected override async Task RunInternal()
        {
            var (grainId, protocolVersion, siloAddress) = await ConnectionPreamble.Read(this.Context);

            if (protocolVersion >= NetworkProtocolVersion.Version2)
            {
                await ConnectionPreamble.Write(
                    this.Context,
                    Constants.SiloDirectConnectionId,
                    this.connectionOptions.ProtocolVersion,
                    this.myAddress);
            }

            if (grainId.Equals(Constants.SiloDirectConnectionId))
            {
                throw new InvalidOperationException($"Unexpected direct silo connection on proxy endpoint from {siloAddress?.ToString() ?? "unknown silo"}");
            }

            // refuse clients that are connecting to the wrong cluster
            if (grainId.Category == UniqueKey.Category.GeoClient)
            {
                if (grainId.Key.ClusterId != this.siloDetails.ClusterId)
                {
                    var message = string.Format(
                            "Refusing connection by client {0} because of cluster id mismatch: client={1} silo={2}",
                            grainId, grainId.Key.ClusterId, this.siloDetails.ClusterId);
                    this.Log.Error(ErrorCode.GatewayAcceptor_WrongClusterId, message);
                    throw new InvalidOperationException(message);
                }
            }
            else
            {
                //convert handshake cliendId to a GeoClient ID 
                if (this.multiClusterOptions.HasMultiClusterNetwork)
                {
                    grainId = GrainId.NewClientId(grainId.PrimaryKey, this.siloDetails.ClusterId);
                }
            }

            try
            {
                this.gateway.RecordOpenedConnection(this, grainId);
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
                msg.DropExpiredMessage(MessagingStatisticsGroup.Phase.Send);
                return false;
            }

            // Fill in the outbound message with our silo address, if it's not already set
            if (msg.SendingSilo == null)
                msg.SendingSilo = this.myAddress;

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
                    this.myAddress,
                    msg,
                    exc);

                MessagingStatisticsGroup.OnDroppedSentMessage(msg);
            }
        }

        protected override void OnSendMessageFailure(Message message, string error)
        {
            this.FailMessage(message, error);
        }
    }
}
