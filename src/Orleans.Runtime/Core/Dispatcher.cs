using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime
{
    internal class Dispatcher
    {
        private readonly MessageCenter messageCenter;
        private readonly RuntimeMessagingTrace messagingTrace;
        private readonly SiloAddress _siloAddress;
        private readonly OrleansTaskScheduler scheduler;
        private readonly Catalog catalog;
        private readonly ILogger logger;
        private readonly SiloMessagingOptions messagingOptions;
        private readonly PlacementService placementService;
        private readonly MessageFactory messageFactory;
        private readonly ActivationDirectory activationDirectory;
        private readonly ILocalGrainDirectory localGrainDirectory;

        internal Dispatcher(
            OrleansTaskScheduler scheduler,
            MessageCenter messageCenter,
            Catalog catalog,
            IOptionsMonitor<SiloMessagingOptions> messagingOptions,
            PlacementService placementService,
            ILocalGrainDirectory localGrainDirectory,
            MessageFactory messageFactory,
            ILoggerFactory loggerFactory,
            ActivationDirectory activationDirectory,
            RuntimeMessagingTrace messagingTrace,
            ILocalSiloDetails localSiloDetails)
        {
            _siloAddress = localSiloDetails.SiloAddress;
            this.scheduler = scheduler;
            this.catalog = catalog;
            this.messageCenter = messageCenter;
            this.messagingOptions = messagingOptions.CurrentValue;
            this.placementService = placementService;
            this.localGrainDirectory = localGrainDirectory;
            this.messageFactory = messageFactory;
            this.activationDirectory = activationDirectory;
            this.messagingTrace = messagingTrace;
            this.logger = loggerFactory.CreateLogger<Dispatcher>();
        }

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

                var str = string.Format("{0} {1}", rejectInfo ?? "", exc == null ? "" : exc.ToString());
                var rejection = this.messageFactory.CreateRejectionResponse(message, rejectionType, str, exc);
                messageCenter.SendMessage(rejection);
            }
            else
            {
                this.messagingTrace.OnDispatcherDiscardedRejection(message, rejectionType, rejectInfo, exc);
            }
        }

        internal void ProcessRequestsToInvalidActivation(
            List<Message> messages,
            ActivationAddress oldAddress,
            ActivationAddress forwardingAddress,
            string failedOperation,
            Exception exc = null,
            bool rejectMessages = false)
        {
            // IMPORTANT: do not do anything on activation context anymore, since this activation is invalid already.
            scheduler.QueueAction(
                () =>
                {
                    foreach (var message in messages)
                    {
                        if (rejectMessages)
                        {
                            RejectMessage(message, Message.RejectionTypes.Transient, exc, failedOperation);
                        }
                        else
                        {
                            this.messagingTrace.OnDispatcherForwardingMultiple(messages.Count, oldAddress, forwardingAddress, failedOperation, exc);
                            TryForwardRequest(message, oldAddress, forwardingAddress, failedOperation, exc);
                        }
                    }
                },
                catalog);
        }

        internal void ProcessRequestToInvalidActivation(
            Message message,
            ActivationAddress oldAddress,
            ActivationAddress forwardingAddress,
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

        public void ProcessRequestToStuckActivation(
            Message message,
            ActivationData activationData,
            string failedOperation)
        {
            scheduler.RunOrQueueTask(
                   async () =>
                   {
                       await catalog.DeactivateStuckActivation(activationData);
                       TryForwardRequest(message, activationData.Address, activationData.ForwardingAddress, failedOperation);
                   },
                   catalog)
                .Ignore();
        }

        internal void TryForwardRequest(Message message, ActivationAddress oldAddress, ActivationAddress forwardingAddress, string failedOperation, Exception exc = null)
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
                        var str = $"Forwarding failed: tried to forward message {message} for {message.ForwardCount} times after {failedOperation} to invalid activation. Rejecting now.";
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

        internal bool TryForwardMessage(Message message, ActivationAddress forwardingAddress)
        {
            if (!MayForward(message, this.messagingOptions)) return false;

            message.ForwardCount = message.ForwardCount + 1;
            MessagingProcessingStatisticsGroup.OnDispatcherMessageForwared(message);
            ResendMessageImpl(message, forwardingAddress);
            return true;
        }

        private void ResendMessageImpl(Message message, ActivationAddress forwardingAddress = null)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Resend {0}", message);
            message.TargetHistory = message.GetTargetHistory();

            if (message.TargetGrain.IsSystemTarget())
            {
                this.PrepareSystemTargetMessage(message);
                this.messageCenter.SendMessage(message);
            }
            else if (forwardingAddress != null)
            {
                message.TargetAddress = forwardingAddress;
                message.IsNewPlacement = false;
                this.messageCenter.SendMessage(message);
            }
            else
            {
                message.TargetActivation = default;
                message.TargetSilo = null;
                message.ClearTargetAddress();
                this.SendMessage(message);
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
        internal Task SendMessage(Message message, IGrainContext sendingActivation = null)
        {
            try
            {
                var messageAddressingTask = placementService.AddressMessage(message);
                if (messageAddressingTask.Status != TaskStatus.RanToCompletion)
                {
                    return SendMessageAsync(messageAddressingTask, message, sendingActivation);
                }

                messageCenter.SendMessage(message);
            }
            catch (Exception ex)
            {
                OnAddressingFailure(message, sendingActivation, ex);
            }

            return Task.CompletedTask;

            async Task SendMessageAsync(Task addressMessageTask, Message m, IGrainContext activation)
            {
                try
                {
                    await addressMessageTask;
                }
                catch (Exception ex)
                {
                    OnAddressingFailure(m, activation, ex);
                    return;
                }

                messageCenter.SendMessage(m);
            }

            void OnAddressingFailure(Message m, IGrainContext activation, Exception ex)
            {
                this.messagingTrace.OnDispatcherSelectTargetFailed(m, activation, ex);
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
                PrepareSystemTargetMessage(message);
            }

            messageCenter.SendMessage(message);
        }

        internal void PrepareSystemTargetMessage(Message message)
        {
            message.Category = message.TargetGrain.Equals(Constants.MembershipServiceType) ?
                Message.Categories.Ping : Message.Categories.System;

            if (message.TargetSilo == null)
            {
                message.TargetSilo = _siloAddress;
            }

            if (message.TargetActivation is null)
            {
                message.TargetActivation = ActivationId.GetDeterministic(message.TargetGrain);
            }
        }

        public void ReceiveMessage(Message msg)
        {
            this.messagingTrace.OnIncomingMessageAgentReceiveMessage(msg);

            // Find the activation it targets; first check for a system activation, then an app activation
            if (msg.TargetGrain.IsSystemTarget())
            {
                SystemTarget target = this.activationDirectory.FindSystemTarget(msg.TargetActivation);
                if (target == null)
                {
                    MessagingStatisticsGroup.OnRejectedMessage(msg);
                    this.logger.LogWarning(
                        (int) ErrorCode.MessagingMessageFromUnknownActivation,
                        "Received a message {Message} for an unknown SystemTarget: {Target}",
                         msg, msg.TargetAddress);

                    // Send a rejection only on a request
                    if (msg.Direction == Message.Directions.Request)
                    {
                        var response = this.messageFactory.CreateRejectionResponse(
                            msg,
                            Message.RejectionTypes.Unrecoverable,
                            $"SystemTarget {msg.TargetGrain} not active on this silo. Msg={msg}");

                        this.messageCenter.SendMessage(response);
                    }
                    return;
                }

                target.ReceiveMessage(msg);
            }
            else if (messageCenter.TryDeliverToProxy(msg))
            {
                return;
            }
            else
            {
                try
                {
                    var targetActivation = catalog.GetOrCreateActivation(
                        msg.TargetAddress,
                        msg.IsNewPlacement,
                        msg.RequestContextData);

                    if (targetActivation is null)
                    {
                        // Activation does not exists and is not a new placement.
                        if (msg.Direction == Message.Directions.Response)
                        {
                            logger.LogWarning(
                                (int)ErrorCode.Dispatcher_NoTargetActivation,
                                "No target activation {Activation} for response message: {Message}",
                                msg.TargetActivation,
                                msg);
                            return;
                        }
                        else
                        {
                            logger.LogInformation(
                                (int)ErrorCode.Dispatcher_Intermediate_GetOrCreateActivation,
                                "Intermediate NonExistentActivation for message {Message}",
                                msg);

                            var nonExistentActivation = msg.TargetAddress;
                            ProcessRequestToInvalidActivation(msg, nonExistentActivation, null, "Non-existent activation");
                            return;
                        }
                    }

                    targetActivation.ReceiveMessage(msg);
                }
                catch (Exception ex)
                {
                    MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedError(msg);
                    logger.LogError(
                        (int)ErrorCode.Dispatcher_ErrorCreatingActivation,
                        ex,
                        "Error creating activation for grain {TargetGrain} (interface: {InterfaceType}). Message {Message}",
                        msg.TargetGrain,
                        msg.InterfaceType,
                        msg);

                    this.RejectMessage(msg, Message.RejectionTypes.Transient, ex);
                }
            }
        }
    }
}
