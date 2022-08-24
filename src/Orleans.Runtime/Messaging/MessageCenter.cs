using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Placement;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime.Messaging
{
    internal class MessageCenter : IMessageCenter, IAsyncDisposable
    {
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly MessageFactory messageFactory;
        private readonly ConnectionManager connectionManager;
        private readonly RuntimeMessagingTrace messagingTrace;
        private readonly SiloAddress _siloAddress;
        private readonly SiloMessagingOptions messagingOptions;
        private readonly PlacementService placementService;
        private readonly ActivationDirectory activationDirectory;
        private readonly ILocalGrainDirectory localGrainDirectory;
        private readonly ILogger log;
        private readonly Catalog catalog;
        private bool stopped;
        private HostedClient hostedClient;
        private Action<Message> sniffIncomingMessageHandler;

        public MessageCenter(
            ILocalSiloDetails siloDetails,
            MessageFactory messageFactory,
            Catalog catalog,
            Factory<MessageCenter, Gateway> gatewayFactory,
            ILogger<MessageCenter> logger,
            ISiloStatusOracle siloStatusOracle,
            ConnectionManager senderManager,
            RuntimeMessagingTrace messagingTrace,
            IOptions<SiloMessagingOptions> messagingOptions,
            PlacementService placementService,
            ILocalGrainDirectory localGrainDirectory,
            ActivationDirectory activationDirectory)
        {
            this.catalog = catalog;
            this.messagingOptions = messagingOptions.Value;
            this.siloStatusOracle = siloStatusOracle;
            this.connectionManager = senderManager;
            this.messagingTrace = messagingTrace;
            this.placementService = placementService;
            this.localGrainDirectory = localGrainDirectory;
            this.activationDirectory = activationDirectory;
            this.log = logger;
            this.messageFactory = messageFactory;
            this._siloAddress = siloDetails.SiloAddress;

            if (siloDetails.GatewayAddress != null)
            {
                Gateway = gatewayFactory(this);
            }
        }

        public Gateway Gateway { get; }

        internal bool IsBlockingApplicationMessages { get; private set; }

        public void SetHostedClient(HostedClient client) => this.hostedClient = client;

        public bool TryDeliverToProxy(Message msg)
        {
            if (!msg.TargetGrain.IsClient()) return false;
            if (this.Gateway is Gateway gateway && gateway.TryDeliverToProxy(msg)) return true;
            return this.hostedClient is HostedClient client && client.TryDispatchToClient(msg);
        }

        public async Task StopAsync()
        {
            BlockApplicationMessages();
            await StopAcceptingClientMessages();
            stopped = true;
        }

        /// <summary>
        /// Indicates that application messages should be blocked from being sent or received.
        /// This method is used by the "fast stop" process.
        /// <para>
        /// Specifically, all outbound application messages are dropped, except for rejections and messages to the membership table grain.
        /// Inbound application requests are rejected, and other inbound application messages are dropped.
        /// </para>
        /// </summary>
        public void BlockApplicationMessages()
        {
            if (log.IsEnabled(LogLevel.Debug))
            {
                log.LogDebug("BlockApplicationMessages");
            }

            IsBlockingApplicationMessages = true;
        }

        public async Task StopAcceptingClientMessages()
        {
            if (log.IsEnabled(LogLevel.Debug))
            {
                log.LogDebug("StopClientMessages");
            }

            try
            {
                if (Gateway is { } gateway)
                {
                    await gateway.StopAsync();
                }
            }
            catch (Exception exc)
            {
                log.LogError((int)ErrorCode.Runtime_Error_100109, exc, "Stop failed");
            }
        }

        public Action<Message> SniffIncomingMessage
        {
            set
            {
                if (this.sniffIncomingMessageHandler != null)
                {
                    throw new InvalidOperationException("MessageCenter.SniffIncomingMessage already set");
                }

                this.sniffIncomingMessageHandler = value;
            }

            get => this.sniffIncomingMessageHandler;
        }

        public void SendMessage(Message msg)
        {
            // Note that if we identify or add other grains that are required for proper stopping, we will need to treat them as we do the membership table grain here.
            if (IsBlockingApplicationMessages && !msg.IsSystemMessage && msg.Result is not Message.ResponseTypes.Rejection && !Constants.SystemMembershipTableType.Equals(msg.TargetGrain))
            {
                // Drop the message on the floor if it's an application message that isn't a rejection
                this.messagingTrace.OnDropBlockedApplicationMessage(msg);
            }
            else
            {
                msg.SendingSilo ??= _siloAddress;

                if (stopped)
                {
                    log.LogInformation((int)ErrorCode.Runtime_Error_100115, "Message was queued for sending after outbound queue was stopped: {Message}", msg);
                    SendRejection(msg, Message.RejectionTypes.Unrecoverable, "Message was queued for sending after outbound queue was stopped");
                    return;
                }

                // Don't process messages that have already timed out
                if (msg.IsExpired)
                {
                    this.messagingTrace.OnDropExpiredMessage(msg, MessagingInstruments.Phase.Send);
                    return;
                }

                // First check to see if it's really destined for a proxied client, instead of a local grain.
                if (TryDeliverToProxy(msg))
                {
                    // Message was successfully delivered to the proxy.
                    return;
                }

                if (msg.TargetSilo is not { } targetSilo)
                {
                    log.LogError((int)ErrorCode.Runtime_Error_100113, "Message does not have a target silo: " + msg + " -- Call stack is: " + Utils.GetStackTrace());
                    SendRejection(msg, Message.RejectionTypes.Unrecoverable, "Message to be sent does not have a target silo");
                    return;
                }

                messagingTrace.OnSendMessage(msg);
                if (targetSilo.Matches(_siloAddress))
                {
                    if (log.IsEnabled(LogLevel.Trace))
                    {
                        log.LogTrace("Message has been looped back to this silo: {Message}", msg);
                    }

                    MessagingInstruments.LocalMessagesSentCounterAggregator.Add(1);

                    this.ReceiveMessage(msg);
                }
                else
                {
                    if (stopped)
                    {
                        log.LogInformation((int)ErrorCode.Runtime_Error_100115, "Message was queued for sending after outbound queue was stopped: {Message}", msg);
                        SendRejection(msg, Message.RejectionTypes.Unrecoverable, "Message was queued for sending after outbound queue was stopped");
                        return;
                    }

                    if (this.connectionManager.TryGetConnection(targetSilo, out var existingConnection))
                    {
                        existingConnection.Send(msg);
                        return;
                    }
                    else if (this.siloStatusOracle.IsDeadSilo(targetSilo))
                    {
                        // Do not try to establish
                        this.messagingTrace.OnRejectSendMessageToDeadSilo(_siloAddress, msg);
                        this.SendRejection(msg, Message.RejectionTypes.Transient, "Target silo is known to be dead");
                        return;
                    }
                    else
                    {
                        var connectionTask = this.connectionManager.GetConnection(targetSilo);
                        if (connectionTask.IsCompletedSuccessfully)
                        {
                            var sender = connectionTask.Result;
                            sender.Send(msg);
                        }
                        else
                        {
                            _ = SendAsync(this, connectionTask, msg);

                            static async Task SendAsync(MessageCenter messageCenter, ValueTask<Connection> connectionTask, Message msg)
                            {
                                try
                                {
                                    var sender = await connectionTask;
                                    sender.Send(msg);
                                }
                                catch (Exception exception)
                                {
                                    messageCenter.SendRejection(msg, Message.RejectionTypes.Transient, $"Exception while sending message: {exception}");
                                }
                            }
                        }
                    }
                }
            }
        }

        public void DispatchLocalMessage(Message message) => ReceiveMessage(message);

        public void RejectMessage(
            Message message,
            Message.RejectionTypes rejectionType,
            Exception exc,
            string rejectInfo = null)
        {
            if (message.Direction == Message.Directions.Request
                || (message.Direction == Message.Directions.OneWay && message.HasCacheInvalidationHeader))
            {
                this.messagingTrace.OnDispatcherRejectMessage(message, rejectionType, rejectInfo, exc);

                var str = $"{rejectInfo} {exc}";
                var rejection = this.messageFactory.CreateRejectionResponse(message, rejectionType, str, exc);
                SendMessage(rejection);
            }
            else
            {
                this.messagingTrace.OnDispatcherDiscardedRejection(message, rejectionType, rejectInfo, exc);
            }
        }

        internal void ProcessRequestsToInvalidActivation(
            List<Message> messages,
            GrainAddress oldAddress,
            GrainAddress forwardingAddress,
            string failedOperation = null,
            Exception exc = null,
            bool rejectMessages = false)
        {
            if (rejectMessages)
            {
                foreach (var message in messages)
                {
                    if (oldAddress != null)
                    {
                        message.AddToCacheInvalidationHeader(oldAddress);
                    }

                    RejectMessage(message, Message.RejectionTypes.Transient, exc, failedOperation);
                }
            }
            else
            {
                this.messagingTrace.OnDispatcherForwardingMultiple(messages.Count, oldAddress, forwardingAddress, failedOperation, exc);
                foreach (var message in messages)
                {
                    TryForwardRequest(message, oldAddress, forwardingAddress, failedOperation, exc);
                }
            }
        }

        internal void ProcessRequestToInvalidActivation(
            Message message,
            GrainAddress oldAddress,
            GrainAddress forwardingAddress,
            string failedOperation,
            Exception exc = null,
            bool rejectMessages = false)
        {
            // Just use this opportunity to invalidate local Cache Entry as well.
            if (oldAddress != null)
            {
                this.localGrainDirectory.InvalidateCacheEntry(oldAddress);
            }

            // IMPORTANT: do not do anything on activation context anymore, since this activation is invalid already.
            if (rejectMessages)
            {
                this.RejectMessage(message, Message.RejectionTypes.Transient, exc, failedOperation);
            }
            else
            {
                this.TryForwardRequest(message, oldAddress, forwardingAddress, failedOperation, exc);
            }
        }

        internal void TryForwardRequest(Message message, GrainAddress oldAddress, GrainAddress forwardingAddress, string failedOperation = null, Exception exc = null)
        {
            bool forwardingSucceded = false;
            try
            {
                this.messagingTrace.OnDispatcherForwarding(message, oldAddress, forwardingAddress, failedOperation, exc);

                if (oldAddress != null)
                {
                    message.AddToCacheInvalidationHeader(oldAddress);
                }

                forwardingSucceded = this.TryForwardMessage(message, forwardingAddress);
            }
            catch (Exception exc2)
            {
                forwardingSucceded = false;
                exc = exc2;
            }
            finally
            {
                var sentRejection = false;

                // If the message was a one-way message, send a cache invalidation response even if the message was successfully forwarded.
                if (message.Direction == Message.Directions.OneWay)
                {
                    this.RejectMessage(
                        message,
                        Message.RejectionTypes.CacheInvalidation,
                        exc,
                        "OneWay message sent to invalid activation");
                    sentRejection = true;
                }

                if (!forwardingSucceded)
                {
                    this.messagingTrace.OnDispatcherForwardingFailed(message, oldAddress, forwardingAddress, failedOperation, exc);
                    if (!sentRejection)
                    {
                        var str = $"Forwarding failed: tried to forward message {message} for {message.ForwardCount} times after \"{failedOperation}\" to invalid activation. Rejecting now.";
                        RejectMessage(message, Message.RejectionTypes.Transient, exc, str);
                    }
                }
            }
        }

        /// <summary>
        /// Reroute a message coming in through a gateway
        /// </summary>
        /// <param name="message"></param>
        internal void RerouteMessage(Message message)
        {
            ResendMessageImpl(message);
        }

        internal bool TryForwardMessage(Message message, GrainAddress forwardingAddress)
        {
            if (!MayForward(message, this.messagingOptions)) return false;

            message.ForwardCount = message.ForwardCount + 1;
            MessagingProcessingInstruments.OnDispatcherMessageForwared(message);
            ResendMessageImpl(message, forwardingAddress);
            return true;
        }

        private void ResendMessageImpl(Message message, GrainAddress forwardingAddress = null)
        {
            if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Resend {Message}", message);

            if (message.TargetGrain.IsSystemTarget())
            {
                message.IsSystemMessage = true;
                SendMessage(message);
            }
            else if (forwardingAddress != null)
            {
                message.TargetSilo = forwardingAddress.SiloAddress;
                SendMessage(message);
            }
            else
            {
                message.TargetSilo = null;
                _ = AddressAndSendMessage(message);
            }
        }

        // Forwarding is used by the receiver, usually when it cannot process the message and forwards it to another silo to perform the processing
        // (got here due to duplicate activation, outdated cache, silo is shutting down/overloaded, ...).
        private static bool MayForward(Message message, SiloMessagingOptions messagingOptions)
        {
            return message.ForwardCount < messagingOptions.MaxForwardCount;
        }

        /// <summary>
        /// Send an outgoing message, may complete synchronously
        /// - may buffer for transaction completion / commit if it ends a transaction
        /// - choose target placement address, maintaining send order
        /// - add ordering info and maintain send order
        ///
        /// </summary>
        internal Task AddressAndSendMessage(Message message)
        {
            try
            {
                var messageAddressingTask = placementService.AddressMessage(message);
                if (messageAddressingTask.Status != TaskStatus.RanToCompletion)
                {
                    return SendMessageAsync(messageAddressingTask, message);
                }

                SendMessage(message);
            }
            catch (Exception ex)
            {
                OnAddressingFailure(message, ex);
            }

            return Task.CompletedTask;

            async Task SendMessageAsync(Task addressMessageTask, Message m)
            {
                try
                {
                    await addressMessageTask;
                }
                catch (Exception ex)
                {
                    OnAddressingFailure(m, ex);
                    return;
                }

                SendMessage(m);
            }

            void OnAddressingFailure(Message m, Exception ex)
            {
                this.messagingTrace.OnDispatcherSelectTargetFailed(m, ex);
                RejectMessage(m, Message.RejectionTypes.Unrecoverable, ex);
            }
        }

        internal void SendResponse(Message request, Response response)
        {
            // create the response
            var message = this.messageFactory.CreateResponseMessage(request);
            message.BodyObject = response;

            if (message.TargetGrain.IsSystemTarget())
            {
                message.IsSystemMessage = true;
            }

            SendMessage(message);
        }

        public void ReceiveMessage(Message msg)
        {
            try
            {
                this.messagingTrace.OnIncomingMessageAgentReceiveMessage(msg);
                if (TryDeliverToProxy(msg))
                {
                    return;
                }
                else if (msg.Direction == Message.Directions.Response)
                {
                    this.catalog.RuntimeClient.ReceiveResponse(msg);
                }
                else
                {
                    var targetActivation = catalog.GetOrCreateActivation(
                        msg.TargetGrain,
                        msg.RequestContextData);

                    if (targetActivation is null)
                    {
                        ProcessMessageToNonExistentActivation(msg);
                        return;
                    }

                    targetActivation.ReceiveMessage(msg);
                }
            }
            catch (Exception ex)
            {
                HandleReceiveFailure(msg, ex);
            }

            void HandleReceiveFailure(Message msg, Exception ex)
            {
                MessagingProcessingInstruments.OnDispatcherMessageProcessedError(msg);
                log.LogError(
                    (int)ErrorCode.Dispatcher_ErrorCreatingActivation,
                    ex,
                    "Error creating activation for grain {TargetGrain} (interface: {InterfaceType}). Message {Message}",
                    msg.TargetGrain,
                    msg.InterfaceType,
                    msg);

                this.RejectMessage(msg, Message.RejectionTypes.Transient, ex);
            }
        }

        private void ProcessMessageToNonExistentActivation(Message msg)
        {
            var target = msg.TargetGrain;
            if (target.IsSystemTarget())
            {
                MessagingInstruments.OnRejectedMessage(msg);
                this.log.LogWarning(
                    (int) ErrorCode.MessagingMessageFromUnknownActivation,
                    "Received a message {Message} for an unknown SystemTarget: {Target}",
                     msg,
                     msg.TargetGrain);

                // Send a rejection only on a request
                if (msg.Direction == Message.Directions.Request)
                {
                    var response = this.messageFactory.CreateRejectionResponse(
                        msg,
                        Message.RejectionTypes.Unrecoverable,
                        $"SystemTarget {msg.TargetGrain} not active on this silo. Msg={msg}");

                    SendMessage(response);
                }
            }
            else
            {
                // Activation does not exists and is not a new placement.
                log.LogInformation(
                    (int)ErrorCode.Dispatcher_Intermediate_GetOrCreateActivation,
                    "Intermediate NonExistentActivation for message {Message}",
                    msg);

                var nonExistentActivation = new GrainAddress { SiloAddress = msg.TargetSilo, GrainId = msg.TargetGrain };
                ProcessRequestToInvalidActivation(msg, nonExistentActivation, null, "Non-existent activation");
            }
        }

        internal void SendRejection(Message msg, Message.RejectionTypes rejectionType, string reason)
        {
            MessagingInstruments.OnRejectedMessage(msg);

            if (msg.Direction is Message.Directions.Response && msg.Result is Message.ResponseTypes.Rejection)
            {
                // Do not send reject a rejection locally, it will create a stack overflow
                MessagingInstruments.OnDroppedSentMessage(msg);
                if (this.log.IsEnabled(LogLevel.Debug)) log.LogDebug("Dropping rejection {Message}", msg);
            }
            else
            {
                if (string.IsNullOrEmpty(reason)) reason = $"Rejection from silo {this._siloAddress} - Unknown reason.";
                var error = this.messageFactory.CreateRejectionResponse(msg, rejectionType, reason);
                // rejection msgs are always originated in the local silo, they are never remote.
                this.ReceiveMessage(error);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }
    }
}
