using System;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Versions;
using Orleans.Runtime.Versions.Compatibility;

namespace Orleans.Runtime
{
    /// <summary>
    /// Responsible for scheduling incoming messages for an activation.
    /// </summary>
    internal class ActivationMessageScheduler
    {
        private readonly Dispatcher _dispatcher;
        private readonly IncomingRequestMonitor _incomingRequestMonitor;
        private readonly Catalog _catalog;
        private readonly GrainVersionManifest _versionManifest;
        private readonly RuntimeMessagingTrace _messagingTrace;
        private readonly ActivationCollector _activationCollector;
        private readonly CompatibilityDirectorManager _compatibilityDirectorManager;
        private readonly OrleansTaskScheduler _scheduler;

        public ActivationMessageScheduler(
            Catalog catalog,
            Dispatcher dispatcher,
            GrainVersionManifest versionManifest,
            RuntimeMessagingTrace messagingTrace,
            ActivationCollector activationCollector,
            OrleansTaskScheduler scheduler,
            CompatibilityDirectorManager compatibilityDirectorManager,
            IncomingRequestMonitor incomingRequestMonitor)
        {
            _incomingRequestMonitor = incomingRequestMonitor;
            _catalog = catalog;
            _versionManifest = versionManifest;
            _messagingTrace = messagingTrace;
            _activationCollector = activationCollector;
            _scheduler = scheduler;
            _compatibilityDirectorManager = compatibilityDirectorManager;
            _dispatcher = dispatcher;
        }

        /// <summary>
        /// Receive a new message:
        /// - validate order constraints, queue (or possibly redirect) if out of order
        /// - validate transactions constraints
        /// - invoke handler if ready, otherwise enqueue for later invocation
        /// </summary>
        public void ReceiveMessage(IGrainContext target, Message message)
        {
            var activation = (ActivationData)target;
            _messagingTrace.OnDispatcherReceiveMessage(message);

            // Don't process messages that have already timed out
            if (message.IsExpired)
            {
                MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedError(message);
                _messagingTrace.OnDropExpiredMessage(message, MessagingStatisticsGroup.Phase.Dispatch);
                return;
            }

            if (message.Direction == Message.Directions.Response)
            {
                ReceiveResponse(message, activation);
            }
            else // Request or OneWay
            {
                if (activation.State == ActivationState.Valid)
                {
                    _activationCollector.TryRescheduleCollection(activation);
                }

                // Silo is always capable to accept a new request. It's up to the activation to handle its internal state.
                // If activation is shutting down, it will queue and later forward this request.
                ReceiveRequest(message, activation);
            }
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

        /// <summary>
        /// Enqueue message for local handling after transaction completes
        /// </summary>
        /// <param name="message"></param>
        /// <param name="targetActivation"></param>
        private void EnqueueRequest(Message message, ActivationData targetActivation)
        {
            var overloadException = targetActivation.CheckOverloaded();
            if (overloadException != null)
            {
                MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedError(message);
                _dispatcher.RejectMessage(message, Message.RejectionTypes.Overloaded, overloadException, "Target activation is overloaded " + targetActivation);
                return;
            }

            switch (targetActivation.EnqueueMessage(message))
            {
                case ActivationData.EnqueueMessageResult.Success:
                    // Great, nothing to do
                    break;
                case ActivationData.EnqueueMessageResult.ErrorInvalidActivation:
                    _dispatcher.ProcessRequestToInvalidActivation(message, targetActivation.Address, targetActivation.ForwardingAddress, "EnqueueRequest");
                    break;
                case ActivationData.EnqueueMessageResult.ErrorActivateFailed:
                    _dispatcher.ProcessRequestToInvalidActivation(message, targetActivation.Address, targetActivation.ForwardingAddress, "EnqueueRequest", rejectMessages: true);
                    break;
                case ActivationData.EnqueueMessageResult.ErrorStuckActivation:
                    // Avoid any new call to this activation
                    _dispatcher.ProcessRequestToStuckActivation(message, targetActivation, "EnqueueRequest - blocked grain");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            // Dont count this as end of processing. The message will come back after queueing via HandleIncomingRequest.
        }

        private void ReceiveResponse(Message message, ActivationData targetActivation)
        {
            lock (targetActivation)
            {
                if (targetActivation.State == ActivationState.Invalid || targetActivation.State == ActivationState.FailedToActivate)
                {
                    _messagingTrace.OnDispatcherReceiveInvalidActivation(message, targetActivation.State);
                    return;
                }

                MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedOk(message);

                _catalog.RuntimeClient.ReceiveResponse(message);
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
                // If the grain was previously inactive, schedule it for workload analysis
                if (targetActivation.IsInactive)
                {
                    _incomingRequestMonitor.MarkRecentlyUsed(targetActivation);
                }

                if (!ActivationMayAcceptRequest(targetActivation, message))
                {
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
            if (incoming.IsAlwaysInterleave)
            {
                return true;
            }

            if (targetActivation.Blocking is null)
            {
                return true;
            }

            if (targetActivation.Blocking.IsReadOnly && incoming.IsReadOnly)
            {
                return true;
            }

            if (targetActivation.GetComponent<GrainCanInterleave>() is GrainCanInterleave canInterleave)
            {
                return canInterleave.MayInterleave(incoming);
            }

            return false;
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
                    _dispatcher.ProcessRequestToInvalidActivation(
                        message,
                        targetActivation.Address,
                        targetActivation.ForwardingAddress,
                        "HandleIncomingRequest",
                        rejectMessages: targetActivation.State == ActivationState.FailedToActivate);
                    return;
                }

                if (message.InterfaceVersion > 0)
                {
                    var compatibilityDirector = _compatibilityDirectorManager.GetDirector(message.InterfaceType);
                    var currentVersion = _versionManifest.GetLocalVersion(message.InterfaceType);
                    if (!compatibilityDirector.IsCompatible(message.InterfaceVersion, currentVersion))
                    {
                        _catalog.DeactivateActivationOnIdle(targetActivation);
                        _dispatcher.ProcessRequestToInvalidActivation(
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
                _messagingTrace.OnScheduleMessage(message);
                _scheduler.QueueWorkItem(new InvokeWorkItem(targetActivation, message, _catalog.RuntimeClient, this));
            }
        }
    }
}
