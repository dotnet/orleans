using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;
using Orleans.Internal;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

internal sealed partial class ActivationData
{
    private static async Task RunMessageLoop(ActivationData activation)
    {
        while (true)
        {
            try
            {
                if (!activation.IsCurrentlyExecuting)
                {
                    bool hasPendingOperations;
                    lock (activation._lock)
                    {
                        hasPendingOperations = activation._operations.HasPending;
                    }

                    if (hasPendingOperations)
                    {
                        await ProcessOperationsAsync(activation);
                    }
                }

                ProcessPendingRequests(activation);
                await activation._messagePump.WaitAsync();
            }
            catch (Exception exception)
            {
                LogErrorInGrainMessageLoop(activation._shared.Logger, exception);
            }
        }
    }

    private static void ProcessPendingRequests(ActivationData activation)
    {
        var index = 0;
        while (true)
        {
            Message? message;
            lock (activation._lock)
            {
                if (!activation._requests.TryGetWaiting(index, out message))
                {
                    return;
                }

                if (activation.State != ActivationState.Valid
                    && !(message.IsLocalOnly && activation.State is ActivationState.Deactivating))
                {
                    ProcessRequestsToInvalidActivation(activation);
                    return;
                }

                try
                {
                    if (!MayInvokeRequest(activation, message))
                    {
                        ++index;
                        if (activation._requests.BlockingRequest is { } blockingRequest)
                        {
                            var activeTime = activation._requests.BusyDuration.Elapsed;
                            if (activeTime > activation._shared.MaxRequestProcessingTime && !activation.IsStuckProcessingMessage)
                            {
                                activation.DeactivateStuckActivation();
                            }
                            else if (activeTime > activation._shared.MaxWarningRequestProcessingTime)
                            {
                                LogWarningDispatcher_ExtendedMessageProcessing(
                                    activation._shared.Logger,
                                    activeTime,
                                    new(activation),
                                    blockingRequest,
                                    message);
                            }
                        }

                        continue;
                    }

                    if (message.InterfaceVersion > 0)
                    {
                        var compatibilityDirector = activation._shared.InternalRuntime.CompatibilityDirectorManager.GetDirector(message.InterfaceType);
                        var currentVersion = activation._shared.InternalRuntime.GrainVersionManifest.GetLocalVersion(message.InterfaceType);
                        if (!compatibilityDirector.IsCompatible(message.InterfaceVersion, currentVersion))
                        {
                            message.AddToCacheInvalidationHeader(activation.Address, validAddress: null);
                            activation.Deactivate(
                                new(
                                    DeactivationReasonCode.IncompatibleRequest,
                                    $"Received incompatible request for interface {message.InterfaceType} version {message.InterfaceVersion}. This activation supports interface version {currentVersion}."),
                                message.RequestContextData.TryGetActivityContext(),
                                cancellationToken: default);
                            return;
                        }
                    }
                }
                catch (Exception exception)
                {
                    if (!message.IsLocalOnly)
                    {
                        activation._shared.InternalRuntime.MessageCenter.RejectMessage(message, Message.RejectionTypes.Transient, exception);
                    }

                    activation._requests.RemoveWaitingAt(index);
                    continue;
                }

                activation._requests.RemoveWaitingAt(index);
                Debug.Assert(activation.State == ActivationState.Valid || message.IsLocalOnly);
                activation._requests.RecordRunning(message);
            }

            activation.InvokeIncomingRequest(message);
        }
    }

    private static bool MayInvokeRequest(ActivationData activation, Message incoming)
    {
        var isReentrant = incoming.GetReentrancyId() is Guid id && activation.IsReentrantSection(id);
        try
        {
            return activation._requests.MayInvoke(
                incoming,
                isReentrant,
                activation._extras?.InterleavingPredicate ?? activation._shared.InterleavingPredicate,
                activation.GrainInstance);
        }
        catch (Exception exception)
        {
            LogErrorInvokingMayInterleavePredicate(activation._shared.Logger, exception, activation, incoming);
            throw;
        }
    }

    private static void ProcessRequestsToInvalidActivation(ActivationData activation)
    {
        if (activation.State is ActivationState.Creating or ActivationState.Activating)
        {
            return;
        }

        if (activation.State is ActivationState.Deactivating)
        {
            var deactivatingTime = activation.GrainRuntime.TimeProvider.GetUtcNow().UtcDateTime - activation.DeactivationStartTime!.Value;
            if (deactivatingTime > activation._shared.MaxRequestProcessingTime && !activation.IsStuckDeactivating)
            {
                activation.IsStuckDeactivating = true;
                if (activation.DeactivationReason.Description is { Length: > 0 }
                    && activation.DeactivationReason.ReasonCode != DeactivationReasonCode.ActivationUnresponsive)
                {
                    activation.DeactivationReason = new(
                        DeactivationReasonCode.ActivationUnresponsive,
                        $"{activation.DeactivationReason.Description}. Activation {activation} has been deactivating since {activation.DeactivationStartTime.Value} and is likely stuck");
                }

                AbandonStuckDeactivatingActivation(activation);
            }

            if (!activation.IsStuckDeactivating && !activation.IsStuckProcessingMessage)
            {
                return;
            }
        }

        if (activation.DeactivationException is null || activation.ForwardingAddress is { })
        {
            activation.RerouteAllQueuedMessages();
        }
        else
        {
            activation.RejectAllQueuedMessages();
        }
    }

    private static void AbandonStuckDeactivatingActivation(ActivationData activation)
    {
        var forwardingAddress = activation.ForwardingAddress;
        LogWarningAbandoningStuckDeactivatingActivation(activation._shared.Logger, activation, forwardingAddress);
        activation.ForwardingAddress = null;
        activation.UnregisterMessageTarget();
        activation._shared.InternalRuntime.GrainLocator.Unregister(activation.Address, UnregistrationCause.Force).Ignore();
        activation.GetDeactivationCompletionSource().TrySetResult(true);
    }

    /// <summary>
    /// Handle an incoming message and queue/invoke appropriate handler
    /// </summary>
    /// <param name="message"></param>
    private void InvokeIncomingRequest(Message message)
    {
        _shared.MessagingProcessingInstruments.OnDispatcherMessageProcessedOk(message);

        try
        {
            var task = _shared.InternalRuntime.RuntimeClient.Invoke(this, message);

            // Note: This runs for all outcomes - both Success or Fault
            if (task.IsCompleted)
            {
                OnCompletedRequest(message);
            }
            else
            {
                _ = OnCompleteAsync(this, message, task);
            }
        }
        catch
        {
            OnCompletedRequest(message);
        }

        static async ValueTask OnCompleteAsync(ActivationData activation, Message message, Task task)
        {
            try
            {
                await task;
            }
            catch
            {
            }
            finally
            {
                activation.OnCompletedRequest(message);
            }
        }
    }

    /// <summary>
    /// Invoked when an activation has finished a transaction and may be ready for additional transactions
    /// </summary>
    /// <param name="message">The message that has just completed processing.</param>
    private void OnCompletedRequest(Message message)
    {
        lock (_lock)
        {
            _requests.Complete(message);

            // If the message is meant to keep the activation active, reset the idle timer and ensure the activation
            // is in the activation working set.
            if (message.IsKeepAlive)
            {
                _idleDuration = CoarseStopwatch.StartNew();

                if (!_isInWorkingSet)
                {
                    _isInWorkingSet = true;
                    _shared.InternalRuntime.ActivationWorkingSet.OnActive(this);
                }
            }

        }

        // Signal the message pump to see if there is another request which can be processed now that this one has completed
        _messagePump.Signal();
    }


    /// <summary>
    /// Rejects all messages enqueued for the provided activation.
    /// </summary>
    private void RejectAllQueuedMessages()
    {
        lock (_lock)
        {
            List<Message> msgs = DequeueAllWaitingRequests();
            if (msgs == null || msgs.Count <= 0) return;

            LogRejectAllQueuedMessages(_shared.Logger, msgs.Count, this);
            _shared.InternalRuntime.GrainLocator.InvalidateCache(Address);
            _shared.InternalRuntime.MessageCenter.ProcessRequestsToInvalidActivation(
                msgs,
                Address,
                forwardingAddress: ForwardingAddress,
                failedOperation: DeactivationReason.Description,
                exc: DeactivationException,
                rejectMessages: true);
        }
    }

    private void RerouteAllQueuedMessages()
    {
        lock (_lock)
        {
            List<Message> msgs = DequeueAllWaitingRequests();
            if (msgs is not { Count: > 0 })
            {
                return;
            }

            if (_shared.Logger.IsEnabled(LogLevel.Debug))
            {
                if (ForwardingAddress is { } address)
                {
                    LogReroutingMessages(_shared.Logger, msgs.Count, this, address);
                }
                else
                {
                    LogReroutingMessagesNoForwarding(_shared.Logger, msgs.Count, this);
                }
            }

            _shared.InternalRuntime.GrainLocator.InvalidateCache(Address);
            _shared.InternalRuntime.MessageCenter.ProcessRequestsToInvalidActivation(msgs, Address, ForwardingAddress, DeactivationReason.Description, DeactivationException);
        }
    }

    ValueTask IGrainCallCancellationExtension.CancelRequestAsync(GrainId senderGrainId, CorrelationId messageId)
        => this.RunOrQueueTask(
            static state => CancelRequestAsyncCore(state.activation, state.senderGrainId, state.messageId),
            (activation: this, senderGrainId, messageId));

    private static ValueTask CancelRequestAsyncCore(ActivationData activation, GrainId senderGrainId, CorrelationId messageId)
    {
        if (!TryCancelRequest(activation, senderGrainId, messageId))
        {
            // The message being canceled may not have arrived yet, so retry a few times.
            return RetryCancellationAfterDelay(activation, senderGrainId, messageId);
        }

        return ValueTask.CompletedTask;
    }

    private static async ValueTask RetryCancellationAfterDelay(
        ActivationData activation,
        GrainId senderGrainId,
        CorrelationId messageId)
    {
        var attemptsRemaining = 3;
        do
        {
            await Task.Delay(1_000);
        } while (!TryCancelRequest(activation, senderGrainId, messageId) && --attemptsRemaining > 0);
    }

    private static bool TryCancelRequest(ActivationData activation, GrainId senderGrainId, CorrelationId messageId)
    {
        Message? message = null;
        var wasWaiting = false;
        lock (activation._lock)
        {
            activation._requests.TryFindRequest(senderGrainId, messageId, out message, out wasWaiting);
        }

        var didCancel = false;
        if (message is not null && message.BodyObject is IInvokable request)
        {
            if (wasWaiting)
            {
                // If the request was waiting, then we necessarily did manage to cancel it, so send the response now.
                activation._shared.InternalRuntime.RuntimeClient.SendResponse(message, Response.FromException(new OperationCanceledException()));
                didCancel = true;
            }
            else
            {
                didCancel = TryCancelInvokable(activation, request) || !request.IsCancellable;
            }
        }

        return didCancel;
    }

    private static bool TryCancelInvokable(ActivationData activation, IInvokable request)
    {
        try
        {
            return request.TryCancel();
        }
        catch (Exception exception)
        {
            LogErrorCancellationCallbackFailed(activation.Shared.Logger, exception);
            return true;
        }
    }
}
