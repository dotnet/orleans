using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Versions.Compatibility;

namespace Orleans.Runtime
{
    internal class Dispatcher
    {
        internal MessageCenter Transport { get; }

        private readonly OrleansTaskScheduler scheduler;
        private readonly Catalog catalog;
        private readonly ILogger logger;
        private readonly SiloMessagingOptions messagingOptions;
        private readonly PlacementDirectorsManager placementDirectorsManager;
        private readonly ILocalGrainDirectory localGrainDirectory;
        private readonly IGrainLocator grainLocator;
        private readonly ActivationCollector activationCollector;
        private readonly MessageFactory messageFactory;
        private readonly CompatibilityDirectorManager compatibilityDirectorManager;
        private readonly RuntimeMessagingTrace messagingTrace;
        private readonly GrainTypeManager grainTypeManager;
        private readonly SchedulingOptions schedulingOptions;
        private readonly ILogger invokeWorkItemLogger;

        internal Dispatcher(
            OrleansTaskScheduler scheduler,
            MessageCenter transport, 
            Catalog catalog,
            IOptions<SiloMessagingOptions> messagingOptions,
            PlacementDirectorsManager placementDirectorsManager,
            ILocalGrainDirectory localGrainDirectory,
            IGrainLocator grainLocator,
            ActivationCollector activationCollector,
            MessageFactory messageFactory,
            CompatibilityDirectorManager compatibilityDirectorManager,
            ILoggerFactory loggerFactory,
            IOptions<SchedulingOptions> schedulerOptions,
            RuntimeMessagingTrace messagingTrace,
            GrainTypeManager grainTypeManager)
        {
            this.scheduler = scheduler;
            this.catalog = catalog;
            Transport = transport;
            this.messagingOptions = messagingOptions.Value;
            this.invokeWorkItemLogger = loggerFactory.CreateLogger<InvokeWorkItem>();
            this.placementDirectorsManager = placementDirectorsManager;
            this.localGrainDirectory = localGrainDirectory;
            this.grainLocator = grainLocator;
            this.activationCollector = activationCollector;
            this.messageFactory = messageFactory;
            this.compatibilityDirectorManager = compatibilityDirectorManager;
            this.messagingTrace = messagingTrace;
            this.grainTypeManager = grainTypeManager;
            this.schedulingOptions = schedulerOptions.Value;
            logger = loggerFactory.CreateLogger<Dispatcher>();
        }

        public ISiloRuntimeClient RuntimeClient => this.catalog.RuntimeClient;

        /// <summary>
        /// Receive a new message:
        /// - validate order constraints, queue (or possibly redirect) if out of order
        /// - validate transactions constraints
        /// - invoke handler if ready, otherwise enqueue for later invocation
        /// </summary>
        /// <param name="message"></param>
        public void ReceiveMessage(Message message)
        {
            this.messagingTrace.OnDispatcherReceiveMessage(message);

            // Don't process messages that have already timed out
            if (message.IsExpired)
            {
                MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedError(message);
                this.messagingTrace.OnDropExpiredMessage(message, MessagingStatisticsGroup.Phase.Dispatch);
                return;
            }

            // check if its targeted at a new activation
            if (message.TargetGrain.IsSystemTarget())
            {
                MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedError(message);
                throw new InvalidOperationException("Dispatcher was called ReceiveMessage on system target for " + message);
            }

            try
            {
                Task ignore;
                ActivationData target = catalog.GetOrCreateActivation(
                    message.TargetAddress, 
                    message.IsNewPlacement,
                    message.NewGrainType,
                    String.IsNullOrEmpty(message.GenericGrainType) ? null : message.GenericGrainType, 
                    message.RequestContextData,
                    out ignore);

                if (ignore != null)
                {
                    ignore.Ignore();
                }

                if (message.Direction == Message.Directions.Response)
                {
                    ReceiveResponse(message, target);
                }
                else // Request or OneWay
                {
                    if (target.State == ActivationState.Valid)
                    {
                        this.activationCollector.TryRescheduleCollection(target);
                    }
                    // Silo is always capable to accept a new request. It's up to the activation to handle its internal state.
                    // If activation is shutting down, it will queue and later forward this request.
                    ReceiveRequest(message, target);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedError(message);
              
                    var nea = ex as Catalog.NonExistentActivationException;
                    if (nea == null)
                    {
                        var str = $"Error creating activation for {message.NewGrainType}. Message {message}";
                        logger.Error(ErrorCode.Dispatcher_ErrorCreatingActivation, str, ex);
                        throw new OrleansException(str, ex);
                    }

                    if (nea.IsStatelessWorker)
                    {
                        if (logger.IsEnabled(LogLevel.Debug)) logger.Debug(ErrorCode.Dispatcher_Intermediate_GetOrCreateActivation,
                           $"Intermediate StatelessWorker NonExistentActivation for message {message}, Exception {ex}");
                    }
                    else
                    {
                        logger.Info(ErrorCode.Dispatcher_Intermediate_GetOrCreateActivation,
                            $"Intermediate NonExistentActivation for message {message}, with Exception {ex}");
                    }

                    ActivationAddress nonExistentActivation = nea.NonExistentActivation;

                    if (message.Direction != Message.Directions.Response)
                    {
                        // Un-register the target activation so we don't keep getting spurious messages.
                        // The time delay (one minute, as of this writing) is to handle the unlikely but possible race where
                        // this request snuck ahead of another request, with new placement requested, for the same activation.
                        // If the activation registration request from the new placement somehow sneaks ahead of this un-registration,
                        // we want to make sure that we don't un-register the activation we just created.
                        // We would add a counter here, except that there's already a counter for this in the Catalog.
                        // Note that this has to run in a non-null scheduler context, so we always queue it to the catalog's context
                        var origin = message.SendingSilo;
                        scheduler.QueueAction(
                            // don't use message.TargetAddress, cause it may have been removed from the headers by this time!
                            async () =>
                            {
                                try
                                {
                                    if (this.logger.IsEnabled(LogLevel.Trace))
                                        logger.Trace("UnregisterAfterNonexistingActivation addr={ActivationAddress} origin={SiloAddress}", nonExistentActivation, origin);

                                    await this.grainLocator.Unregister(nonExistentActivation, UnregistrationCause.NonexistentActivation);
                                }
                                catch (Exception exc)
                                {
                                    logger.Warn(ErrorCode.Dispatcher_FailedToUnregisterNonExistingAct,
                                        $"Failed to un-register NonExistentActivation {nonExistentActivation}", exc);
                                }
                            },
                            catalog);

                        ProcessRequestToInvalidActivation(message, nonExistentActivation, null, "Non-existent activation");
                    }
                    else
                    {
                        logger.Warn(
                            ErrorCode.Dispatcher_NoTargetActivation,
                            nonExistentActivation.Silo.IsClient
                                ? "No target client {0} for response message: {1}. It's likely that the client recently disconnected."
                                : "No target activation {0} for response message: {1}",
                            nonExistentActivation,
                            message);

                        this.localGrainDirectory.InvalidateCacheEntry(nonExistentActivation);
                    }
                }
                catch (Exception exc)
                {
                    // Unable to create activation for this request - reject message
                    RejectMessage(message, Message.RejectionTypes.Transient, exc);
                }
            }
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
                Message rejection = this.messageFactory.CreateRejectionResponse(message, rejectionType, str, exc);
                SendRejectionMessage(rejection);
            }
            else
            {
                this.messagingTrace.OnDispatcherDiscardedRejection(message, rejectionType, rejectInfo, exc);
            }
        }

        internal void SendRejectionMessage(Message rejection)
        {
            if (rejection.Result == Message.ResponseTypes.Rejection)
            {
                Transport.SendMessage(rejection);
            }
            else
            {
                throw new InvalidOperationException(
                    "Attempt to invoke Dispatcher.SendRejectionMessage() for a message that isn't a rejection.");
            }
        }

        private void ReceiveResponse(Message message, ActivationData targetActivation)
        {
            lock (targetActivation)
            {
                if (targetActivation.State == ActivationState.Invalid || targetActivation.State == ActivationState.FailedToActivate)
                {
                    this.messagingTrace.OnDispatcherReceiveInvalidActivation(message, targetActivation.State);
                    return;
                }

                MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedOk(message);
                if (Transport.TryDeliverToProxy(message)) return;

               this.catalog.RuntimeClient.ReceiveResponse(message);
            }
        }

        /// <summary>
        /// Check if we can locally accept this message.
        /// Redirects if it can't be accepted.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="targetActivation"></param>
        private void ReceiveRequest(Message message, ActivationData targetActivation)
        {
            lock (targetActivation)
            {
                if (!ActivationMayAcceptRequest(targetActivation, message))
                {
                    // Check for deadlock before Enqueueing.
                    if (schedulingOptions.PerformDeadlockDetection && !message.TargetGrain.IsSystemTarget())
                    {
                        try
                        {
                            CheckDeadlock(message);
                        }
                        catch (DeadlockException exc)
                        {
                            // Record that this message is no longer flowing through the system
                            this.messagingTrace.OnDispatcherDetectedDeadlock(message, targetActivation, exc);

                            // We want to send DeadlockException back as an application exception, rather than as a system rejection.
                            SendResponse(message, Response.ExceptionResponse(exc));
                            return;
                        }
                    }

                    EnqueueRequest(message, targetActivation);
                }
                else
                {
                    HandleIncomingRequest(message, targetActivation);
                }
            }
        }

        /// <summary>
        /// Determine if the activation is able to currently accept the given message
        /// - always accept responses
        /// For other messages, require that:
        /// - activation is properly initialized
        /// - the message would not cause a reentrancy conflict
        /// </summary>
        /// <param name="targetActivation"></param>
        /// <param name="incoming"></param>
        /// <returns></returns>
        private bool ActivationMayAcceptRequest(ActivationData targetActivation, Message incoming)
        {
            if (targetActivation.State != ActivationState.Valid) return false;
            if (!targetActivation.IsCurrentlyExecuting) return true;
            return CanInterleave(targetActivation, incoming);
        }

        /// <summary>
        /// Whether an incoming message can interleave 
        /// </summary>
        /// <param name="targetActivation"></param>
        /// <param name="incoming"></param>
        /// <returns></returns>
        public bool CanInterleave(ActivationData targetActivation, Message incoming)
        {
            bool canInterleave = 
                   incoming.IsAlwaysInterleave
                || targetActivation.Blocking == null
                || (targetActivation.Blocking.IsReadOnly && incoming.IsReadOnly)
                || (schedulingOptions.AllowCallChainReentrancy && targetActivation.ActivationId.Equals(incoming.SendingActivation))
                || catalog.CanInterleave(targetActivation.ActivationId, incoming);

            return canInterleave;
        }

        /// <summary>
        /// https://github.com/dotnet/orleans/issues/3184
        /// Checks whether reentrancy is allowed for calls to grains that are already part of the call chain.
        /// Covers following case: grain A calls grain B, and while executing the invoked method B calls back to A. 
        /// Design: Senders collection `RunningRequestsSenders` contains sending grains references
        /// during duration of request processing. If target of outgoing request is found in that collection - 
        /// such request will be marked as interleaving in order to prevent deadlocks.
        /// </summary>
        private void MarkSameCallChainMessageAsInterleaving(IGrainContext sendingActivation, Message outgoing)
        {
            if (!schedulingOptions.AllowCallChainReentrancy)
            {
                return;
            }

            if ((sendingActivation as ActivationData)?.RunningRequestsSenders.Contains(outgoing.TargetActivation) == true)
            {
                outgoing.IsAlwaysInterleave = true;
            }
        }

        /// <summary>
        /// Check if the current message will cause deadlock.
        /// Throw DeadlockException if yes.
        /// </summary>
        /// <param name="message">Message to analyze</param>
        private void CheckDeadlock(Message message)
        {
            var requestContext = message.RequestContextData;
            object obj;
            if (requestContext == null ||
                !requestContext.TryGetValue(RequestContext.CALL_CHAIN_REQUEST_CONTEXT_HEADER, out obj) ||
                obj == null) return; // first call in a chain

            var prevChain = ((IList)obj);
            ActivationId nextActivationId = message.TargetActivation;
            // check if the target activation already appears in the call chain.
            foreach (object invocationObj in prevChain)
            {
                var prevId = ((RequestInvocationHistory)invocationObj).ActivationId;
                if (!prevId.Equals(nextActivationId) || catalog.CanInterleave(nextActivationId, message)) continue;

                var newChain = new List<RequestInvocationHistory>();
                newChain.AddRange(prevChain.Cast<RequestInvocationHistory>());
                newChain.Add(new RequestInvocationHistory(message.TargetGrain, message.TargetActivation));

                throw new DeadlockException(
                    string.Format(
                        "Deadlock Exception for grain call chain {0}.",
                        Utils.EnumerableToString(
                            newChain,
                            elem => elem.GrainId.ToString())),
                    newChain.Select(req => req.GrainId).ToList());
            }
        }

        /// <summary>
        /// Handle an incoming message and queue/invoke appropriate handler
        /// </summary>
        /// <param name="message"></param>
        /// <param name="targetActivation"></param>
        public void HandleIncomingRequest(Message message, ActivationData targetActivation)
        {
            lock (targetActivation)
            {
                if (targetActivation.State == ActivationState.Invalid || targetActivation.State == ActivationState.FailedToActivate)
                {
                    ProcessRequestToInvalidActivation(
                        message,
                        targetActivation.Address,
                        targetActivation.ForwardingAddress,
                        "HandleIncomingRequest",
                        rejectMessages: targetActivation.State == ActivationState.FailedToActivate);
                    return;
                }

                if (targetActivation.Grain.IsLegacyGrain() && message.IsUsingInterfaceVersions)
                {
                    var request = (InvokeMethodRequest)message.BodyObject;
                    var compatibilityDirector = compatibilityDirectorManager.GetDirector(request.InterfaceId);
                    var currentVersion = this.grainTypeManager.GetLocalSupportedVersion(request.InterfaceId);
                    if (!compatibilityDirector.IsCompatible(request.InterfaceVersion, currentVersion))
                    {
                        catalog.DeactivateActivationOnIdle(targetActivation);
                        ProcessRequestToInvalidActivation(
                            message,
                            targetActivation.Address,
                            targetActivation.ForwardingAddress,
                            "HandleIncomingRequest - Incompatible request");
                        return;
                    }
                }

                // Now we can actually scheduler processing of this request
                targetActivation.RecordRunning(message, message.IsAlwaysInterleave);

                MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedOk(message);
                this.messagingTrace.OnScheduleMessage(message);
                scheduler.QueueWorkItem(new InvokeWorkItem(targetActivation, message, this, this.invokeWorkItemLogger));
            }
        }

        /// <summary>
        /// Enqueue message for local handling after transaction completes
        /// </summary>
        /// <param name="message"></param>
        /// <param name="targetActivation"></param>
        private void EnqueueRequest(Message message, ActivationData targetActivation)
        {
            var overloadException = targetActivation.CheckOverloaded(logger);
            if (overloadException != null)
            {
                MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedError(message);
                RejectMessage(message, Message.RejectionTypes.Overloaded, overloadException, "Target activation is overloaded " + targetActivation);
                return;
            }

            switch (targetActivation.EnqueueMessage(message))
            {
                case ActivationData.EnqueueMessageResult.Success:
                    // Great, nothing to do
                    break;
                case ActivationData.EnqueueMessageResult.ErrorInvalidActivation:
                    ProcessRequestToInvalidActivation(message, targetActivation.Address, targetActivation.ForwardingAddress, "EnqueueRequest");
                    break;
                case ActivationData.EnqueueMessageResult.ErrorActivateFailed:
                    ProcessRequestToInvalidActivation(message, targetActivation.Address, targetActivation.ForwardingAddress, "EnqueueRequest", rejectMessages: true);
                    break;
                case ActivationData.EnqueueMessageResult.ErrorStuckActivation:
                    // Avoid any new call to this activation
                    catalog.DeactivateStuckActivation(targetActivation);
                    ProcessRequestToInvalidActivation(message, targetActivation.Address, targetActivation.ForwardingAddress, "EnqueueRequest - blocked grain");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            // Dont count this as end of processing. The message will come back after queueing via HandleIncomingRequest.

#if DEBUG
            // This is a hot code path, so using #if to remove diags from Release version
            // Note: Caller already holds lock on activation
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace(ErrorCode.Dispatcher_EnqueueMessage,
                "EnqueueMessage for {0}: targetActivation={1}", message.TargetActivation, targetActivation.DumpStatus());
#endif
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
                scheduler.QueueAction(
                    () => RejectMessage(message, Message.RejectionTypes.Transient, exc, failedOperation),
                    catalog);
            }
            else
            {
                scheduler.QueueAction(
                    () => TryForwardRequest(message, oldAddress, forwardingAddress, failedOperation, exc),
                    catalog);
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
            // Just use this opportunity to invalidate local Cache Entry as well. 
            if (oldAddress != null)
            {
                this.localGrainDirectory.InvalidateCacheEntry(oldAddress);
            }

            this.messagingTrace.OnDispatcherForwardingMultiple(messages.Count, oldAddress, forwardingAddress, failedOperation, exc);

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
                            TryForwardRequest(message, oldAddress, forwardingAddress, failedOperation, exc);
                        }
                    }
                },
                catalog);
        }

        internal void TryForwardRequest(Message message, ActivationAddress oldAddress, ActivationAddress forwardingAddress, string failedOperation, Exception exc = null)
        {
            bool forwardingSucceded = true;
            try
            {
                this.messagingTrace.OnDispatcherForwarding(message, oldAddress, forwardingAddress, failedOperation, exc);

                // if this message is from a different cluster and hit a non-existing activation
                // in this cluster (which can happen due to stale cache or directory states)
                // we forward it back to the original silo it came from in the original cluster,
                // and target it to a fictional activation that is guaranteed to not exist.
                // This ensures that the GSI protocol creates a new instance there instead of here.
                if (forwardingAddress == null
                    && message.TargetSilo != message.SendingSilo
                    && !this.localGrainDirectory.IsSiloInCluster(message.SendingSilo))
                {
                    message.IsReturnedFromRemoteCluster = true; // marks message to force invalidation of stale directory entry
                    forwardingAddress = ActivationAddress.NewActivationAddress(message.SendingSilo, message.TargetGrain);
                    logger.Info(ErrorCode.Messaging_Dispatcher_ReturnToOriginCluster, $"Forwarding back to origin cluster, to fictional activation {message}");
                }

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
                this.SendSystemTargetMessage(message);
            }
            else if (forwardingAddress != null)
            {
                message.TargetAddress = forwardingAddress;
                message.IsNewPlacement = false;
                this.Transport.SendMessage(message);
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
            return message.ForwardCount < messagingOptions.MaxForwardCount
                // allow one more forward hop for multi-cluster case
                + (message.IsReturnedFromRemoteCluster ? 1 : 0);
        }

        /// <summary>
        /// Send an outgoing message, may complete synchronously
        /// - may buffer for transaction completion / commit if it ends a transaction
        /// - choose target placement address, maintaining send order
        /// - add ordering info and maintain send order
        /// 
        /// </summary>
        /// <param name="message"></param>
        /// <param name="sendingActivation"></param>
        public Task AsyncSendMessage(Message message, IGrainContext sendingActivation = null)
        {
            try
            {
                var messageAddressingTask = AddressMessage(message);
                if (messageAddressingTask.Status == TaskStatus.RanToCompletion)
                {
                    TransportMessage(message, sendingActivation);
                }
                else
                {
                    return TransportMessageAferSending(messageAddressingTask, message, sendingActivation);
                }
            }
            catch (Exception ex)
            {
                OnAddressingFailure(message, sendingActivation, ex);
            }

            return Task.CompletedTask;

            async Task TransportMessageAferSending(Task addressMessageTask, Message m, IGrainContext activation)
            {
                try
                {
                    await addressMessageTask;
                }
                catch (Exception ex)
                {
                    OnAddressingFailure(message, activation, ex);
                    return;
                }

                TransportMessage(m, activation);
            }

            void OnAddressingFailure(Message m, IGrainContext activation, Exception ex)
            {
                this.messagingTrace.OnDispatcherSelectTargetFailed(m, activation, ex);
                RejectMessage(m, Message.RejectionTypes.Unrecoverable, ex);
            }
        }

        // this is a compatibility method for portions of the code base that don't use
        // async/await yet, which is almost everything. there's no liability to discarding the
        // Task returned by AsyncSendMessage()
        internal void SendMessage(Message message, IGrainContext sendingActivation = null)
        {
            AsyncSendMessage(message, sendingActivation).Ignore();
        }

        /// <summary>
        /// Resolve target address for a message
        /// - use transaction info
        /// - check ordering info in message and sending activation
        /// - use sender's placement strategy
        /// </summary>
        /// <param name="message"></param>
        /// <returns>Resolve when message is addressed (modifies message fields)</returns>
        private Task AddressMessage(Message message)
        {
            var targetAddress = message.TargetAddress;
            if (targetAddress.IsComplete) return Task.CompletedTask;

            // placement strategy is determined by searching for a specification. first, we check for a strategy associated with the grain reference,
            // second, we check for a strategy associated with the target's interface. third, we check for a strategy associated with the activation sending the
            // message.
            var strategy = targetAddress.Grain.IsLegacyGrain() ? catalog.GetGrainPlacementStrategy(targetAddress.Grain) : null;

            var request = message.IsUsingInterfaceVersions
                ? message.BodyObject as InvokeMethodRequest
                : null;
            var target = new PlacementTarget(
                message.TargetGrain,
                message.RequestContextData,
                request?.InterfaceId ?? 0,
                request?.InterfaceVersion ?? 0);

            PlacementResult placementResult;
            if (placementDirectorsManager.TrySelectActivationSynchronously(
                message.SendingAddress, target, this.catalog, strategy, out placementResult) && placementResult != null)
            {
                SetMessageTargetPlacement(message, placementResult, targetAddress);
                return Task.CompletedTask;
            }

           return AddressMessageAsync(message, target, strategy, targetAddress);
        }

        private async Task AddressMessageAsync(Message message, PlacementTarget target, PlacementStrategy strategy, ActivationAddress targetAddress)
        {
            var placementResult = await placementDirectorsManager.SelectOrAddActivation(target, (IPlacementRuntime)this.catalog, strategy);
            SetMessageTargetPlacement(message, placementResult, targetAddress);
        }

        private void SetMessageTargetPlacement(Message message, PlacementResult placementResult, ActivationAddress targetAddress)
        {
            if (placementResult.IsNewPlacement && targetAddress.Grain.IsClient())
            {
                logger.Error(ErrorCode.Dispatcher_AddressMsg_UnregisteredClient, $"AddressMessage could not find target for client pseudo-grain {message}");
                throw new KeyNotFoundException($"Attempting to send a message {message} to an unregistered client pseudo-grain {targetAddress.Grain}");
            }

            message.SetTargetPlacement(placementResult);
            if (placementResult.IsNewPlacement)
            {
                CounterStatistic.FindOrCreate(StatisticNames.DISPATCHER_NEW_PLACEMENT).Increment();
            }
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace(ErrorCode.Dispatcher_AddressMsg_SelectTarget, "AddressMessage Placement SelectTarget {0}", message);
        }

        internal void SendResponse(Message request, Response response)
        {
            // create the response
            var message = this.messageFactory.CreateResponseMessage(request);
            message.BodyObject = response;

            if (message.TargetGrain.IsSystemTarget())
            {
                SendSystemTargetMessage(message);
            }
            else
            {
                TransportMessage(message);
            }
        }

        internal void SendSystemTargetMessage(Message message)
        {
            message.Category = message.TargetGrain.Equals(Constants.MembershipOracleType) ? 
                Message.Categories.Ping : Message.Categories.System;

            if (message.TargetSilo == null)
            {
                message.TargetSilo = Transport.MyAddress;
            }

            if (message.TargetActivation is null)
            {
                message.TargetActivation = ActivationId.GetDeterministic(message.TargetGrain);
            }

            TransportMessage(message);
        }

        /// <summary>
        /// Directly send a message to the transport without processing
        /// </summary>
        /// <param name="message"></param>
        /// <param name="sendingActivation"></param>
        public void TransportMessage(Message message, IGrainContext sendingActivation = null)
        {
            MarkSameCallChainMessageAsInterleaving(sendingActivation, message);
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace(ErrorCode.Dispatcher_Send_AddressedMessage, "Addressed message {0}", message);
            Transport.SendMessage(message);
        }

        /// <summary>
        /// Invoked when an activation has finished a transaction and may be ready for additional transactions
        /// </summary>
        /// <param name="activation">The activation that has just completed processing this message</param>
        /// <param name="message">The message that has just completed processing. 
        /// This will be <c>null</c> for the case of completion of Activate/Deactivate calls.</param>
        internal void OnActivationCompletedRequest(ActivationData activation, Message message)
        {
            lock (activation)
            {
#if DEBUG
                // This is a hot code path, so using #if to remove diags from Release version
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.Trace(ErrorCode.Dispatcher_OnActivationCompletedRequest_Waiting,
                        "OnActivationCompletedRequest {0}: Activation={1}", activation.ActivationId, activation.DumpStatus());
                }
#endif
                activation.ResetRunning(message);

                // ensure inactive callbacks get run even with transactions disabled
                if (!activation.IsCurrentlyExecuting)
                    activation.RunOnInactive();

                // Run message pump to see if there is a new request arrived to be processed
                RunMessagePump(activation);
            }
        }

        internal void RunMessagePump(ActivationData activation)
        {
            // Note: this method must be called while holding lock (activation)
#if DEBUG
            // This is a hot code path, so using #if to remove diags from Release version
            // Note: Caller already holds lock on activation
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.Trace(ErrorCode.Dispatcher_ActivationEndedTurn_Waiting,
                    "RunMessagePump {0}: Activation={1}", activation.ActivationId, activation.DumpStatus());
            }
#endif
            // don't run any messages if activation is not ready or deactivating
            if (activation.State != ActivationState.Valid) return;

            bool runLoop;
            do
            {
                runLoop = false;
                var nextMessage = activation.PeekNextWaitingMessage();
                if (nextMessage == null) continue;
                if (!ActivationMayAcceptRequest(activation, nextMessage)) continue;
                
                activation.DequeueNextWaitingMessage();
                // we might be over-writing an already running read only request.
                HandleIncomingRequest(nextMessage, activation);
                runLoop = true;
            }
            while (runLoop);
        }
    }
}
