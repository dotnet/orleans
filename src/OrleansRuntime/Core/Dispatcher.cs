using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Scheduler;


namespace Orleans.Runtime
{
    internal class Dispatcher
    {
        internal OrleansTaskScheduler Scheduler { get; private set; }
        internal ISiloMessageCenter Transport { get; private set; }

        private readonly Catalog catalog;
        private readonly Logger logger;
        private readonly ClusterConfiguration config;
        private readonly double rejectionInjectionRate;
        private readonly bool errorInjection;
        private readonly double errorInjectionRate;
        private readonly SafeRandom random;

        public Dispatcher(
            OrleansTaskScheduler scheduler, 
            ISiloMessageCenter transport, 
            Catalog catalog, 
            ClusterConfiguration config)
        {
            Scheduler = scheduler;
            this.catalog = catalog;
            Transport = transport;
            this.config = config;
            logger = LogManager.GetLogger("Dispatcher", LoggerType.Runtime);
            rejectionInjectionRate = config.Globals.RejectionInjectionRate;
            double messageLossInjectionRate = config.Globals.MessageLossInjectionRate;
            errorInjection = rejectionInjectionRate > 0.0d || messageLossInjectionRate > 0.0d;
            errorInjectionRate = rejectionInjectionRate + messageLossInjectionRate;
            random = new SafeRandom();
        }

        #region Receive path

        /// <summary>
        /// Receive a new message:
        /// - validate order constraints, queue (or possibly redirect) if out of order
        /// - validate transactions constraints
        /// - invoke handler if ready, otherwise enqueue for later invocation
        /// </summary>
        /// <param name="message"></param>
        public void ReceiveMessage(Message message)
        {
            MessagingProcessingStatisticsGroup.OnDispatcherMessageReceive(message);
            // Don't process messages that have already timed out
            if (message.IsExpired)
            {
                logger.Warn(ErrorCode.Dispatcher_DroppingExpiredMessage, "Dropping an expired message: {0}", message);
                MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedError(message, "Expired");
                message.DropExpiredMessage(MessagingStatisticsGroup.Phase.Dispatch);
                return;
            }

            // check if its targeted at a new activation
            if (message.TargetGrain.IsSystemTarget)
            {
                MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedError(message, "ReceiveMessage on system target.");
                throw new InvalidOperationException("Dispatcher was called ReceiveMessage on system target for " + message);
            }

            if (errorInjection && ShouldInjectError(message))
            {
                if (logger.IsVerbose) logger.Verbose(ErrorCode.Dispatcher_InjectingRejection, "Injecting a rejection");
                MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedError(message, "ErrorInjection");
                RejectMessage(message, Message.RejectionTypes.Unrecoverable, null, "Injected rejection");                
                return;
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
                        catalog.ActivationCollector.TryRescheduleCollection(target);
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
                    MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedError(message, "Non-existent activation");
              
                    var nea = ex as Catalog.NonExistentActivationException;
                    if (nea == null)
                    {
                        var str = String.Format("Error creating activation for {0}. Message {1}", message.NewGrainType, message);
                        logger.Error(ErrorCode.Dispatcher_ErrorCreatingActivation, str, ex);
                        throw new OrleansException(str, ex);
                    }

                    if (nea.IsStatelessWorker)
                    {
                        if (logger.IsVerbose) logger.Verbose(ErrorCode.Dispatcher_Intermediate_GetOrCreateActivation,
                           String.Format("Intermediate StatelessWorker NonExistentActivation for message {0}", message), ex);
                    }
                    else
                    {
                        logger.Info(ErrorCode.Dispatcher_Intermediate_GetOrCreateActivation,
                            String.Format("Intermediate NonExistentActivation for message {0}", message), ex);
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
                        Scheduler.QueueWorkItem(new ClosureWorkItem(
                            // don't use message.TargetAddress, cause it may have been removed from the headers by this time!
                            async () =>
                            {
                                try
                                {
                                    await Silo.CurrentSilo.LocalGrainDirectory.UnregisterAfterNonexistingActivation(
                                        nonExistentActivation, origin);
                                }
                                catch (Exception exc)
                                {
                                    logger.Warn(ErrorCode.Dispatcher_FailedToUnregisterNonExistingAct,
                                        String.Format("Failed to un-register NonExistentActivation {0}",
                                            nonExistentActivation), exc);
                                }
                            },
                            () => "LocalGrainDirectory.UnregisterAfterNonexistingActivation"),
                            catalog.SchedulingContext);

                        ProcessRequestToInvalidActivation(message, nonExistentActivation, null, "Non-existent activation");
                    }
                    else
                    {
                        logger.Warn(ErrorCode.Dispatcher_NoTargetActivation,
                            "No target activation {0} for response message: {1}", nonExistentActivation, message);
                        Silo.CurrentSilo.LocalGrainDirectory.InvalidateCacheEntry(nonExistentActivation);
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
            Message.RejectionTypes rejectType, 
            Exception exc, 
            string rejectInfo = null)
        {
            if (message.Direction == Message.Directions.Request)
            {
                var str = String.Format("{0} {1}", rejectInfo ?? "", exc == null ? "" : exc.ToString());
                MessagingStatisticsGroup.OnRejectedMessage(message);
                Message rejection = message.CreateRejectionResponse(rejectType, str, exc as OrleansException);
                SendRejectionMessage(rejection);
            }
            else
            {
                logger.Warn(ErrorCode.Messaging_Dispatcher_DiscardRejection,
                    "Discarding {0} rejection for message {1}. Exc = {2}",
                    Enum.GetName(typeof(Message.Directions), message.Direction), message, exc == null ? "" : exc.Message);
            }
        }

        internal void SendRejectionMessage(Message rejection)
        {
            if (rejection.Result == Message.ResponseTypes.Rejection)
            {
                Transport.SendMessage(rejection);
                rejection.ReleaseBodyAndHeaderBuffers();
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
                if (targetActivation.State == ActivationState.Invalid)
                {
                    logger.Warn(ErrorCode.Dispatcher_Receive_InvalidActivation,
                        "Response received for invalid activation {0}", message);
                    MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedError(message, "Ivalid");
                    return;
                }
                MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedOk(message);
                if (Transport.TryDeliverToProxy(message)) return;

                RuntimeClient.Current.ReceiveResponse(message);
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
                if (targetActivation.State == ActivationState.Invalid)
                {
                    ProcessRequestToInvalidActivation(
                        message,
                        targetActivation.Address,
                        targetActivation.ForwardingAddress,
                        "ReceiveRequest");
                }
                else if (!ActivationMayAcceptRequest(targetActivation, message))
                {
                    // Check for deadlock before Enqueueing.
                    if (config.Globals.PerformDeadlockDetection && !message.TargetGrain.IsSystemTarget)
                    {
                        try
                        {
                            CheckDeadlock(message);
                        }
                        catch (DeadlockException exc)
                        {
                            // Record that this message is no longer flowing through the system
                            MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedError(message, "Deadlock");
                            logger.Warn(ErrorCode.Dispatcher_DetectedDeadlock, 
                                "Detected Application Deadlock: {0}", exc.Message);
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
                   catalog.IsReentrantGrain(targetActivation.ActivationId)
                || incoming.IsAlwaysInterleave
                || targetActivation.Running == null
                || (targetActivation.Running.IsReadOnly && incoming.IsReadOnly);

            return canInterleave;
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
                if (!prevId.Equals(nextActivationId) || catalog.IsReentrantGrain(nextActivationId)) continue;

                var newChain = new List<RequestInvocationHistory>();
                newChain.AddRange(prevChain.Cast<RequestInvocationHistory>());
                newChain.Add(new RequestInvocationHistory(message));
                
                throw new DeadlockException(newChain);
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
                if (targetActivation.State == ActivationState.Invalid)
                {
                    ProcessRequestToInvalidActivation(message, targetActivation.Address, targetActivation.ForwardingAddress, "HandleIncomingRequest");
                    return;
                }

                // Now we can actually scheduler processing of this request
                targetActivation.RecordRunning(message);
                var context = new SchedulingContext(targetActivation);

                MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedOk(message);
                Scheduler.QueueWorkItem(new InvokeWorkItem(targetActivation, message, context), context);
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
                MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedError(message, "Overload2");
                RejectMessage(message, Message.RejectionTypes.Overloaded, overloadException, "Target activation is overloaded " + targetActivation);
                return;
            }
            
            bool enqueuedOk = targetActivation.EnqueueMessage(message);
            if (!enqueuedOk)
            {
                ProcessRequestToInvalidActivation(message, targetActivation.Address, targetActivation.ForwardingAddress, "EnqueueRequest");
            }

            // Dont count this as end of processing. The message will come back after queueing via HandleIncomingRequest.

#if DEBUG
            // This is a hot code path, so using #if to remove diags from Release version
            // Note: Caller already holds lock on activation
            if (logger.IsVerbose2) logger.Verbose2(ErrorCode.Dispatcher_EnqueueMessage,
                "EnqueueMessage for {0}: targetActivation={1}", message.TargetActivation, targetActivation.DumpStatus());
#endif
        }

        internal void ProcessRequestToInvalidActivation(
            Message message, 
            ActivationAddress oldAddress, 
            ActivationAddress forwardingAddress, 
            string failedOperation, 
            Exception exc = null)
        {
            // Just use this opportunity to invalidate local Cache Entry as well. 
            if (oldAddress != null)
            {
                Silo.CurrentSilo.LocalGrainDirectory.InvalidateCacheEntry(oldAddress);
            }
            // IMPORTANT: do not do anything on activation context anymore, since this activation is invalid already.
            Scheduler.QueueWorkItem(new ClosureWorkItem(
                () => TryForwardRequest(message, oldAddress, forwardingAddress, failedOperation, exc)),
                catalog.SchedulingContext);
        }

        internal void ProcessRequestsToInvalidActivation(
            List<Message> messages,
            ActivationAddress oldAddress,
            ActivationAddress forwardingAddress, 
            string failedOperation,
            Exception exc = null)
        {
            // Just use this opportunity to invalidate local Cache Entry as well. 
            if (oldAddress != null)
            {
                Silo.CurrentSilo.LocalGrainDirectory.InvalidateCacheEntry(oldAddress);
            }
            logger.Info(ErrorCode.Messaging_Dispatcher_ForwardingRequests, 
                String.Format("Forwarding {0} requests to old address {1} after {2}.", messages.Count, oldAddress, failedOperation));

            // IMPORTANT: do not do anything on activation context anymore, since this activation is invalid already.
            Scheduler.QueueWorkItem(new ClosureWorkItem(
                () =>
                {
                    foreach (var message in messages)
                    {
                        TryForwardRequest(message, oldAddress, forwardingAddress, failedOperation, exc);
                    }
                }
                ), catalog.SchedulingContext);
        }

        internal void TryForwardRequest(Message message, ActivationAddress oldAddress, ActivationAddress forwardingAddress, string failedOperation, Exception exc = null)
        {
            bool forwardingSucceded = true;
            try
            {

                logger.Info(ErrorCode.Messaging_Dispatcher_TryForward, 
                    String.Format("Trying to forward after {0}, ForwardCount = {1}. Message {2}.", failedOperation, message.ForwardCount, message));

                // if this message is from a different cluster and hit a non-existing activation
                // in this cluster (which can happen due to stale cache or directory states)
                // we forward it back to the original silo it came from in the original cluster,
                // and target it to a fictional activation that is guaranteed to not exist.
                // This ensures that the GSI protocol creates a new instance there instead of here.
                if (forwardingAddress == null
                    && message.TargetSilo != message.SendingSilo
                    && !Silo.CurrentSilo.LocalGrainDirectory.IsSiloInCluster(message.SendingSilo))
                {
                    message.IsReturnedFromRemoteCluster = true; // marks message to force invalidation of stale directory entry
                    forwardingAddress = ActivationAddress.NewActivationAddress(message.SendingSilo, message.TargetGrain);
                    logger.Info(ErrorCode.Messaging_Dispatcher_ReturnToOriginCluster,
                        String.Format("Forwarding back to origin cluster, to fictional activation {0}", message));
                }

                MessagingProcessingStatisticsGroup.OnDispatcherMessageReRouted(message);
                if (oldAddress != null)
                {
                    message.AddToCacheInvalidationHeader(oldAddress);
                }

                forwardingSucceded = InsideRuntimeClient.Current.TryForwardMessage(message, forwardingAddress);
            }
            catch (Exception exc2)
            {
                forwardingSucceded = false;
                exc = exc2;
            }
            finally
            {
                if (!forwardingSucceded)
                {
                    var str = String.Format("Forwarding failed: tried to forward message {0} for {1} times after {2} to invalid activation. Rejecting now.", 
                        message, message.ForwardCount, failedOperation);
                    logger.Warn(ErrorCode.Messaging_Dispatcher_TryForwardFailed, str, exc);
                    RejectMessage(message, Message.RejectionTypes.Transient, exc, str);
                }
            }
        }

        #endregion

        #region Send path

        /// <summary>
        /// Send an outgoing message
        /// - may buffer for transaction completion / commit if it ends a transaction
        /// - choose target placement address, maintaining send order
        /// - add ordering info and maintain send order
        /// 
        /// </summary>
        /// <param name="message"></param>
        /// <param name="sendingActivation"></param>
        public async Task AsyncSendMessage(Message message, ActivationData sendingActivation = null)
        {
            try
            {
                await AddressMessage(message);
                TransportMessage(message);
            }
            catch (Exception ex)
            {
                if (ShouldLogError(ex))
                {
                    logger.Error(ErrorCode.Dispatcher_SelectTarget_Failed,
                        String.Format("SelectTarget failed with {0}", ex.Message),
                        ex);
                }
                MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedError(message, "SelectTarget failed");
                RejectMessage(message, Message.RejectionTypes.Unrecoverable, ex);
            }
        }

        private bool ShouldLogError(Exception ex)
        {
            return !(ex.GetBaseException() is KeyNotFoundException) &&
                   !(ex.GetBaseException() is ClientNotAvailableException);
        }

        // this is a compatibility method for portions of the code base that don't use
        // async/await yet, which is almost everything. there's no liability to discarding the
        // Task returned by AsyncSendMessage()
        internal void SendMessage(Message message, ActivationData sendingActivation = null)
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
        private async Task AddressMessage(Message message)
        {
            var targetAddress = message.TargetAddress;
            if (targetAddress.IsComplete) return;

            // placement strategy is determined by searching for a specification. first, we check for a strategy associated with the grain reference,
            // second, we check for a strategy associated with the target's interface. third, we check for a strategy associated with the activation sending the
            // message.
            var strategy = targetAddress.Grain.IsGrain ? catalog.GetGrainPlacementStrategy(targetAddress.Grain) : null;
            var placementResult = await PlacementDirectorsManager.Instance.SelectOrAddActivation(
                message.SendingAddress, message.TargetGrain, InsideRuntimeClient.Current.Catalog, strategy);

            if (placementResult.IsNewPlacement && targetAddress.Grain.IsClient)
            {
                logger.Error(ErrorCode.Dispatcher_AddressMsg_UnregisteredClient, String.Format("AddressMessage could not find target for client pseudo-grain {0}", message));
                throw new KeyNotFoundException(String.Format("Attempting to send a message {0} to an unregistered client pseudo-grain {1}", message, targetAddress.Grain));
            }

            message.SetTargetPlacement(placementResult);
            if (placementResult.IsNewPlacement)
            {
                CounterStatistic.FindOrCreate(StatisticNames.DISPATCHER_NEW_PLACEMENT).Increment();
            }
            if (logger.IsVerbose2) logger.Verbose2(ErrorCode.Dispatcher_AddressMsg_SelectTarget, "AddressMessage Placement SelectTarget {0}", message);
       }

        internal void SendResponse(Message request, Response response)
        {
            // create the response
            var message = request.CreateResponseMessage();
            message.BodyObject = response;

            if (message.TargetGrain.IsSystemTarget)
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
            message.Category = message.TargetGrain.Equals(Constants.MembershipOracleId) ? 
                Message.Categories.Ping : Message.Categories.System;

            if (message.TargetSilo == null)
            {
                message.TargetSilo = Transport.MyAddress;
            }
            if (message.TargetActivation == null)
            {
                message.TargetActivation = ActivationId.GetSystemActivation(message.TargetGrain, message.TargetSilo);
            }

            TransportMessage(message);
        }

        /// <summary>
        /// Directly send a message to the transport without processing
        /// </summary>
        /// <param name="message"></param>
        public void TransportMessage(Message message)
        {
            if (logger.IsVerbose2) logger.Verbose2(ErrorCode.Dispatcher_Send_AddressedMessage, "Addressed message {0}", message);
            Transport.SendMessage(message);
        }

        #endregion
        #region Execution

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
                if (logger.IsVerbose2)
                {
                    logger.Verbose2(ErrorCode.Dispatcher_OnActivationCompletedRequest_Waiting,
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
            if (logger.IsVerbose2)
            {
                logger.Verbose2(ErrorCode.Dispatcher_ActivationEndedTurn_Waiting,
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

        private bool ShouldInjectError(Message message)
        {
            if (!errorInjection || message.Direction != Message.Directions.Request) return false;

            double r = random.NextDouble() * 100;
            if (!(r < errorInjectionRate)) return false;

            if (r < rejectionInjectionRate)
            {
                return true;
            }

            if (logger.IsVerbose) logger.Verbose(ErrorCode.Dispatcher_InjectingMessageLoss, "Injecting a message loss");
            // else do nothing and intentionally drop message on the floor to inject a message loss
            return true;
        }

        #endregion
    }
}
