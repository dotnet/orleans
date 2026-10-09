using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Core.Diagnostics;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Placement;
using Orleans.Serialization.Invocation;
using Orleans.Internal;
using Orleans.Connections.Transport;

namespace Orleans.Runtime.Messaging
{
    internal partial class MessageCenter : IMessageCenter, IAsyncDisposable
    {
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly MessageFactory messageFactory;
        private readonly ConnectionManager connectionManager;
        private readonly RuntimeMessagingTrace messagingTrace;
        private readonly MessagingInstruments _messagingInstruments;
        private readonly MessagingProcessingInstruments _messagingProcessingInstruments;
        private readonly SiloAddress _siloAddress;
        private readonly SiloMessagingOptions messagingOptions;
        private readonly PlacementService placementService;
        private readonly GrainLocator _grainLocator;
        private readonly Action<Message>? _messageObserver;
        private readonly ILogger log;
        private readonly Catalog catalog;
        private bool stopped;
        private HostedClient? hostedClient;
        private Action<Message>? sniffIncomingMessageHandler;
        private readonly AdmissionGate _incomingApplicationWork = new();
        private readonly AdmissionGate _addressingWork = new();
        private readonly AdmissionGate _outboundApplicationWork = new();
        private int _retirementStarted;
        private int _retirementSendFailed;

        internal void BeginRetirement() => Volatile.Write(ref _retirementStarted, 1);

        internal void RecordRetirementSendFailure(Message message)
        {
            if (Volatile.Read(ref _retirementStarted) != 0 && message.RequiresApplicationDrain)
            {
                Volatile.Write(ref _retirementSendFailed, 1);
            }
        }

        internal async Task DrainRetirementAsync(CancellationToken cancellationToken)
        {
            await connectionManager.DrainIncomingApplicationDispatchAsync(cancellationToken);
            if (Gateway is { } incomingGateway)
            {
                await incomingGateway.DrainIncomingAsync(cancellationToken);
            }

            await _incomingApplicationWork.CloseAsync().WaitAsync(cancellationToken);
            await _addressingWork.CloseAsync().WaitAsync(cancellationToken);
            await _outboundApplicationWork.CloseAsync().WaitAsync(cancellationToken);
            if (Gateway is { } outgoingGateway)
            {
                await outgoingGateway.DrainOutboundAsync(cancellationToken);
            }

            await connectionManager.DrainAsync(cancellationToken);
            if (Volatile.Read(ref _retirementSendFailed) != 0)
            {
                throw new ConnectionClosedException("Retirement disposition includes unsuccessful outbound work.");
            }
        }

        public MessageCenter(
            ILocalSiloDetails siloDetails,
            MessageFactory messageFactory,
            Catalog catalog,
            Factory<MessageCenter, Gateway> gatewayFactory,
            ILogger<MessageCenter> logger,
            ISiloStatusOracle siloStatusOracle,
            ConnectionManager senderManager,
            RuntimeMessagingTrace messagingTrace,
            MessagingInstruments messagingInstruments,
            MessagingProcessingInstruments messagingProcessingInstruments,
            IOptions<SiloMessagingOptions> messagingOptions,
            PlacementService placementService,
            GrainLocator grainLocator,
            IMessageStatisticsSink messageStatisticsSink)
        {
            this.catalog = catalog;
            this.messagingOptions = messagingOptions.Value;
            this.siloStatusOracle = siloStatusOracle;
            this.connectionManager = senderManager;
            this.messagingTrace = messagingTrace;
            _messagingInstruments = messagingInstruments;
            _messagingProcessingInstruments = messagingProcessingInstruments;
            this.placementService = placementService;
            _grainLocator = grainLocator;
            _messageObserver = messageStatisticsSink.GetMessageObserver();
            this.log = logger;
            this.messageFactory = messageFactory;
            this._siloAddress = siloDetails.SiloAddress;

            if (siloDetails.GatewayAddress != null)
            {
                Gateway = gatewayFactory(this);
            }
        }

        public Gateway? Gateway { get; }

        internal bool IsBlockingApplicationMessages { get; private set; }

        public void SetHostedClient(HostedClient? client) => this.hostedClient = client;

        public bool TryDeliverToProxy(Message msg)
        {
            if (!msg.TargetGrain.IsClient()) return false;
            if (this.Gateway is Gateway gateway && gateway.TryDeliverToProxy(msg)
                || this.hostedClient is HostedClient client && client.TryDispatchToClient(msg))
            {
                _messageObserver?.Invoke(msg);
                return true;
            }

            return false;
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
        /// Specifically, all outbound application requests and one-way messages are rejected or dropped,
        /// while responses and messages to the membership table grain are allowed to complete.
        /// Inbound application requests are rejected with cache invalidation, and other inbound application messages are dropped.
        /// </para>
        /// </summary>
        public void BlockApplicationMessages()
        {
            LogDebugBlockApplicationMessages(log);
            IsBlockingApplicationMessages = true;
        }

        public async Task StopAcceptingClientMessages()
        {
            LogDebugStopClientMessages(log);

            try
            {
                if (Gateway is { } gateway)
                {
                    await gateway.StopAsync();
                }
            }
            catch (Exception exc)
            {
                LogErrorStopFailed(log, exc);
            }
        }

        public Action<Message>? SniffIncomingMessage
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
            var admission = msg.RequiresApplicationDrain ? _outboundApplicationWork.TryEnter() : default;
            if (msg.RequiresApplicationDrain && !admission.Entered)
            {
                this.messagingTrace.OnDropBlockedApplicationMessage(msg);
                msg.Dispose();
                return;
            }

            try
            {
                SendMessageCore(msg, ref admission);
            }
            finally
            {
                admission.Dispose();
            }
        }

        private void SendMessageCore(Message msg, ref AdmissionGate.Admission admission)
        {
            Debug.Assert(!msg.IsLocalOnly);

            // Note that if we identify or add other grains that are required for proper stopping, we will need to treat them as we do the membership table grain here.
            var isBlockedApplicationMessage = IsBlockingApplicationMessages
                && !msg.IsSystemMessage
                && msg.Direction is not Message.Directions.Response
                && !Constants.SystemMembershipTableType.Equals(msg.TargetGrain);
            if (isBlockedApplicationMessage)
            {
                if (msg.Direction == Message.Directions.Request)
                {
                    ProcessRequestToInvalidActivation(
                        msg,
                        new GrainAddress { GrainId = msg.TargetGrain, SiloAddress = msg.TargetSilo },
                        forwardingAddress: null,
                        failedOperation: "Silo stopping",
                        rejectMessages: true);
                }
                else
                {
                    this.messagingTrace.OnDropBlockedApplicationMessage(msg);
                    msg.Dispose();
                }

                return;
            }
            else
            {
                msg.SendingSilo ??= _siloAddress;

                if (stopped)
                {
                    LogInformationMessageQueuedAfterStop(log, msg);
                    SendRejection(msg, Message.RejectionTypes.Unrecoverable, "Message was queued for sending after outbound queue was stopped");
                    return;
                }

                // Don't process messages that have already timed out
                if (msg.IsExpired)
                {
                    this.messagingTrace.OnDropExpiredMessage(msg, MessagingInstruments.Phase.Send);
                    msg.Dispose();
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
                    LogErrorMessageNoTargetSilo(log, msg, new());
                    SendRejection(msg, Message.RejectionTypes.Unrecoverable, "Message to be sent does not have a target silo.");
                    return;
                }

                if (targetSilo.Matches(_siloAddress))
                {
                    LogTraceMessageLoopedBack(log, msg);

                    _messagingInstruments.LocalMessagesSentCounterAggregator.Add(1);

                    this.ReceiveMessage(msg);
                }
                else
                {
                    if (this.connectionManager.TryGetConnection(targetSilo, out var existingConnection))
                    {
                        existingConnection.Send(msg);
                        return;
                    }
                    else if (this.siloStatusOracle.IsDeadSilo(targetSilo))
                    {
                        // Do not try to establish
                        if (msg.Direction is Message.Directions.Request or Message.Directions.OneWay)
                        {
                            this.messagingTrace.OnRejectSendMessageToDeadSilo(_siloAddress, msg);
                            this.SendRejection(msg, Message.RejectionTypes.Transient, "Target silo is known to be dead", new SiloUnavailableException());
                        }
                        else
                        {
                            msg.Dispose();
                        }

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
                            var transferredAdmission = admission;
                            admission = default;
                            _ = SendAsync(this, connectionTask, msg, transferredAdmission);

                            static async Task SendAsync(MessageCenter messageCenter, ValueTask<Connection> connectionTask, Message msg, AdmissionGate.Admission admission)
                            {
                                using var _ = admission;
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
            Exception? exc,
            string? rejectInfo = null)
        {
            try
            {
                if (message.Direction == Message.Directions.Request
                    || (message.Direction == Message.Directions.OneWay && message.HasCacheInvalidationHeader))
                {
                    this.messagingTrace.OnDispatcherRejectMessage(message, rejectionType, rejectInfo, exc);

                    SendRejectionResponse(message, rejectionType, $"{rejectInfo} {exc}", exc);
                }
                else
                {
                    this.messagingTrace.OnDispatcherDiscardedRejection(message, rejectionType, rejectInfo, exc);
                }
            }
            finally
            {
                message.Dispose();
            }
        }

        private void SendRejectionResponse(Message message, Message.RejectionTypes rejectionType, string reason, Exception? exception)
        {
            var rejection = messageFactory.CreateRejectionResponse(message, rejectionType, reason, exception);
            SendMessage(rejection);
        }

        internal void ProcessRequestsToInvalidActivation(
            List<Message> messages,
            GrainAddress? oldAddress,
            SiloAddress? forwardingAddress,
            string failedOperation,
            Exception? exc = null,
            bool rejectMessages = false)
        {
            if (rejectMessages)
            {
                GrainAddress? validAddress = forwardingAddress switch
                {
                    null => null,
                    _ => new()
                    {
                        GrainId = oldAddress?.GrainId ?? default,
                        SiloAddress = forwardingAddress,
                    }
                };

                foreach (var message in messages)
                {
                    Debug.Assert(!message.IsLocalOnly);

                    if (oldAddress != null)
                    {
                        message.AddToCacheInvalidationHeader(oldAddress, validAddress: validAddress);
                    }

                    RejectMessage(message, Message.RejectionTypes.Transient, exc, failedOperation);
                }
            }
            else
            {
                this.messagingTrace.OnDispatcherForwardingMultiple(messages.Count, oldAddress, forwardingAddress, failedOperation, exc);
                GrainAddress? destination = forwardingAddress switch
                {
                    null => null,
                    _ => new()
                    {
                        GrainId = oldAddress?.GrainId ?? default,
                        SiloAddress = forwardingAddress,
                    }
                };

                foreach (var message in messages)
                {
                    TryForwardRequest(message, oldAddress, destination, failedOperation, exc);
                }
            }
        }

        internal void ProcessRequestToInvalidActivation(
            Message message,
            GrainAddress? oldAddress,
            SiloAddress? forwardingAddress,
            string failedOperation,
            Exception? exc = null,
            bool rejectMessages = false)
        {
            Debug.Assert(!message.IsLocalOnly);

            // Just use this opportunity to invalidate local Cache Entry as well.
            if (oldAddress != null)
            {
                _grainLocator.InvalidateCache(oldAddress);
            }

            // IMPORTANT: do not do anything on activation context anymore, since this activation is invalid already.
            if (rejectMessages)
            {
                if (oldAddress != null)
                {
                    message.AddToCacheInvalidationHeader(oldAddress, validAddress: null);
                }

                this.RejectMessage(message, Message.RejectionTypes.Transient, exc, failedOperation);
            }
            else
            {
                GrainAddress? destination = forwardingAddress switch
                {
                    null => null,
                    _ => new()
                    {
                        GrainId = oldAddress?.GrainId ?? default,
                        SiloAddress = forwardingAddress,
                    }
                };
                this.TryForwardRequest(message, oldAddress, destination, failedOperation, exc);
            }
        }

        private void TryForwardRequest(Message message, GrainAddress? oldAddress, GrainAddress? destination, string failedOperation, Exception? exc = null)
        {
            Debug.Assert(!message.IsLocalOnly);

            bool forwardingSucceeded = false;
            var forwardingAddress = destination?.SiloAddress;
            try
            {
                this.messagingTrace.OnDispatcherForwarding(message, oldAddress, forwardingAddress, failedOperation, exc);

                if (oldAddress != null)
                {
                    message.AddToCacheInvalidationHeader(oldAddress, validAddress: destination);
                }

                LogDebugForwarding(log, exc, message, forwardingAddress, failedOperation);
                forwardingSucceeded = this.TryForwardMessage(message, forwardingAddress);
            }
            catch (Exception exc2)
            {
                forwardingSucceeded = false;
                exc = exc2;
            }
            finally
            {
                var sentRejection = false;

                // If the message was a one-way message, send a cache invalidation response even if the message was successfully forwarded.
                if (message.Direction == Message.Directions.OneWay)
                {
                    SendRejectionResponse(
                        message,
                        Message.RejectionTypes.CacheInvalidation,
                        $"OneWay message sent to invalid activation {exc}",
                        exc);
                    sentRejection = true;
                }

                if (!forwardingSucceeded)
                {
                    this.messagingTrace.OnDispatcherForwardingFailed(message, oldAddress, forwardingAddress, failedOperation, exc);
                    if (!sentRejection)
                    {
                        var str = $"Forwarding failed: tried to forward message {message} for {message.ForwardCount} times after \"{failedOperation}\" to invalid activation. Rejecting now.";
                        RejectMessage(message, Message.RejectionTypes.Transient, exc, str);
                    }
                    else
                    {
                        message.Dispose();
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
            var targetSilo = message.IsRelocatableRequest
                && message.TargetSilo is { } hint
                && !siloStatusOracle.IsDeadSilo(hint)
                    ? hint : null;
            ResendMessageImpl(message, targetSilo);
        }

        private bool TryForwardMessage(Message message, SiloAddress? forwardingAddress)
        {
            if (!MayForward(message, this.messagingOptions)) return false;

            message.ForwardCount = message.ForwardCount + 1;
            _messagingProcessingInstruments.OnDispatcherMessageForwared(message);

            ResendMessageImpl(message, forwardingAddress);
            return true;
        }

        private void ResendMessageImpl(Message message, SiloAddress? forwardingAddress = null)
        {
            LogDebugResend(log, message);

            if (message.TargetGrain.IsSystemTarget())
            {
                message.IsSystemMessage = true;
                SendMessage(message);
            }
            else if (forwardingAddress != null)
            {
                message.TargetSilo = forwardingAddress;
                SendForwardingNotice(message);
                SendMessage(message);
            }
            else
            {
                message.TargetSilo = null;
                _ = AddressAndSendMessage(message, notifyForwarding: true);
            }
        }

        private void SendForwardingNotice(Message request)
        {
            if (request.IsRelocatableRequest && request.TargetSilo is { } targetSilo)
            {
                var response = messageFactory.CreateForwardingResponse(request, targetSilo);
                response.SendingSilo = _siloAddress;
                SendMessage(response);
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
        internal Task AddressAndSendMessage(Message message, bool notifyForwarding = false)
        {
            var admission = message.RequiresApplicationDrain ? _addressingWork.TryEnter() : default;
            if (message.RequiresApplicationDrain && !admission.Entered)
            {
                messagingTrace.OnDropBlockedApplicationMessage(message);
                message.Dispose();
                return Task.CompletedTask;
            }

            try
            {
                var messageAddressingTask = placementService.AddressMessage(message);
                if (messageAddressingTask.Status != TaskStatus.RanToCompletion)
                {
                    return SendMessageAsync(messageAddressingTask, message, admission);
                }

                if (notifyForwarding)
                {
                    SendForwardingNotice(message);
                }

                SendMessage(message);
            }
            catch (Exception ex)
            {
                OnAddressingFailure(message, ex);
            }

            admission.Dispose();
            return Task.CompletedTask;

            async Task SendMessageAsync(Task addressMessageTask, Message m, AdmissionGate.Admission admission)
            {
                using var _ = admission;
                try
                {
                    await addressMessageTask;
                }
                catch (Exception ex)
                {
                    OnAddressingFailure(m, ex);
                    return;
                }

                if (notifyForwarding)
                {
                    SendForwardingNotice(m);
                }

                SendMessage(m);
            }

            void OnAddressingFailure(Message m, Exception ex)
            {
                RecordRetirementSendFailure(m);
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
            var applicationRequest = !msg.IsSystemMessage && msg.Direction is Message.Directions.Request or Message.Directions.OneWay;
            using var admission = applicationRequest
                ? _incomingApplicationWork.TryEnter() : default;
            if (applicationRequest && !admission.Entered)
            {
                messagingTrace.OnDropBlockedApplicationMessage(msg);
                msg.Dispose();
                return;
            }

            Debug.Assert(!msg.IsLocalOnly);
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
                        msg.RequestContextData,
                        rehydrationContext: null);

                    if (targetActivation is null)
                    {
                        ProcessMessageToNonExistentActivation(msg);
                        return;
                    }

                    targetActivation.ReceiveMessage(msg);
                    _messageObserver?.Invoke(msg);
                }
            }
            catch (Exception ex)
            {
                HandleReceiveFailure(msg, ex);
            }

            void HandleReceiveFailure(Message msg, Exception ex)
            {
                _messagingProcessingInstruments.OnDispatcherMessageProcessedError(msg);
                LogErrorCreatingActivation(log, ex, msg.TargetGrain, msg.InterfaceType, msg);

                this.RejectMessage(msg, Message.RejectionTypes.Transient, ex);
            }
        }

        private void ProcessMessageToNonExistentActivation(Message msg)
        {
            var target = msg.TargetGrain;
            if (target.IsSystemTarget())
            {
                _messagingInstruments.OnRejectedMessage(msg);
                LogWarningUnknownSystemTarget(log, msg, msg.TargetGrain);

                // Send a rejection only on a request
                if (msg.Direction == Message.Directions.Request)
                {
                    var response = this.messageFactory.CreateRejectionResponse(
                        msg,
                        Message.RejectionTypes.Unrecoverable,
                        $"SystemTarget {msg.TargetGrain} not active on this silo. Msg={msg}");

                    SendMessage(response);
                }

                msg.Dispose();
            }
            else
            {
                // Activation does not exists and is not a new placement.
                LogDebugUnableToCreateActivation(log, msg);

                var partialAddress = new GrainAddress { SiloAddress = msg.TargetSilo, GrainId = msg.TargetGrain };
                ProcessRequestToInvalidActivation(msg, partialAddress, null, failedOperation: "Unable to create local activation");
            }
        }

        internal void SendRejection(Message msg, Message.RejectionTypes rejectionType, string reason, Exception? exception = null)
        {
            RecordRetirementSendFailure(msg);
            try
            {
                _messagingInstruments.OnRejectedMessage(msg);

                if (msg.Direction is Message.Directions.Response && msg.Result is Message.ResponseTypes.Rejection)
                {
                    // Do not send reject a rejection locally, it will create a stack overflow
                    _messagingInstruments.OnDroppedSentMessage(msg);
                    LogDebugDroppingRejection(log, msg);
                }
                else
                {
                    if (string.IsNullOrEmpty(reason)) reason = $"Rejection from silo {this._siloAddress} - Unknown reason.";
                    var error = this.messageFactory.CreateRejectionResponse(msg, rejectionType, reason, exception);
                    // rejection msgs are always originated in the local silo, they are never remote.
                    this.ReceiveMessage(error);
                }
            }
            finally
            {
                msg.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }

        private readonly struct StackTraceLogValue()
        {
            public override string ToString() => Utils.GetStackTrace(1);
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "BlockApplicationMessages"
        )]
        private static partial void LogDebugBlockApplicationMessages(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "StopClientMessages"
        )]
        private static partial void LogDebugStopClientMessages(ILogger logger);

        [LoggerMessage(
            EventId = (int)ErrorCode.Runtime_Error_100109,
            Level = LogLevel.Error,
            Message = "Stop failed"
        )]
        private static partial void LogErrorStopFailed(ILogger logger, Exception exc);

        [LoggerMessage(
            EventId = (int)ErrorCode.Runtime_Error_100115,
            Level = LogLevel.Information,
            Message = "Message was queued for sending after outbound queue was stopped: {Message}"
        )]
        private static partial void LogInformationMessageQueuedAfterStop(ILogger logger, Message message);

        [LoggerMessage(
            EventId = (int)ErrorCode.Runtime_Error_100113,
            Level = LogLevel.Error,
            Message = "Message does not have a target silo: '{Message}'. Call stack: {StackTrace}"
        )]
        private static partial void LogErrorMessageNoTargetSilo(ILogger logger, Message message, StackTraceLogValue stackTrace);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Message has been looped back to this silo: {Message}"
        )]
        private static partial void LogTraceMessageLoopedBack(ILogger logger, Message message);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Forwarding {Message} to '{ForwardingAddress}' after '{FailedOperation}'"
        )]
        private static partial void LogDebugForwarding(ILogger logger, Exception? exc, Message message, SiloAddress? forwardingAddress, string failedOperation);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Resend {Message}"
        )]
        private static partial void LogDebugResend(ILogger logger, Message message);

        [LoggerMessage(
            EventId = (int)ErrorCode.Dispatcher_ErrorCreatingActivation,
            Level = LogLevel.Error,
            Message = "Error creating activation for grain {TargetGrain} (interface: {InterfaceType}). Message {Message}"
        )]
        private static partial void LogErrorCreatingActivation(ILogger logger, Exception ex, GrainId targetGrain, GrainInterfaceType interfaceType, Message message);

        [LoggerMessage(
            EventId = (int)ErrorCode.MessagingMessageFromUnknownActivation,
            Level = LogLevel.Warning,
            Message = "Received a message {Message} for an unknown SystemTarget: {Target}"
        )]
        private static partial void LogWarningUnknownSystemTarget(ILogger logger, Message message, GrainId target);

        [LoggerMessage(
            EventId = (int)ErrorCode.Dispatcher_Intermediate_GetOrCreateActivation,
            Level = LogLevel.Debug,
            Message = "Unable to create local activation for message {Message}."
        )]
        private static partial void LogDebugUnableToCreateActivation(ILogger logger, Message message);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Dropping rejection {Message}"
        )]
        private static partial void LogDebugDroppingRejection(ILogger logger, Message message);
    }
}
