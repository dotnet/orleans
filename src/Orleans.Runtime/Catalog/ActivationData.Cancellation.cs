using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

/// <summary>
/// Cancellation of requests which target this activation.
/// </summary>
internal sealed partial class ActivationData
{
    ValueTask IGrainCallCancellationExtension.CancelRequestAsync(
        GrainId senderGrainId,
        CorrelationId messageId,
        CancellationToken cancellationToken)
        => this.RunOrQueueTask(
            static state => state.self.CancelRequestAsyncCore(state.senderGrainId, state.messageId, state.cancellationToken),
            (self: this, senderGrainId, messageId, cancellationToken));

    private ValueTask CancelRequestAsyncCore(
        GrainId senderGrainId,
        CorrelationId messageId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryCancelOrForward())
        {
            // The request may not have arrived yet, or it may be running without having registered its cancellation source, so retry a few times.
            return RetryCancellationAfterDelay();
        }

        return ValueTask.CompletedTask;

        async ValueTask RetryCancellationAfterDelay()
        {
            var attemptsRemaining = 3;
            do
            {
                await Task.Delay(TimeSpan.FromSeconds(1), GrainRuntime.TimeProvider, cancellationToken);
            } while (!TryCancelOrForward() && --attemptsRemaining > 0);
        }

        bool TryCancelOrForward()
        {
            Message? message = null;
            var wasWaiting = false;
            SiloAddress? forwardTo = null;
            lock (_lock)
            {
                // Check the running requests.
                foreach (var candidate in _runningRequests.Keys)
                {
                    if (candidate.Id == messageId && candidate.SendingGrain == senderGrainId)
                    {
                        message = candidate;
                        break;
                    }
                }

                if (message is null)
                {
                    // Check the waiting requests.
                    for (var i = 0; i < _waitingRequests.Count; i++)
                    {
                        var candidate = _waitingRequests[i].Message;
                        if (candidate.Id == messageId && candidate.SendingGrain == senderGrainId)
                        {
                            message = candidate;
                            _waitingRequests.RemoveAt(i);
                            wasWaiting = true;
                            break;
                        }
                    }
                }

                if (message is null)
                {
                    if (State is ActivationState.Deactivating)
                    {
                        // A deactivating activation holds cancellations as it holds requests. When it finishes deactivating, it applies them to its queued requests,
                        // which include requests which stayed when it migrated, and forwards the rest with its requests.
                        _extras ??= new();
                        (_extras.HeldCancellations ??= []).Add((senderGrainId, messageId));
                        return true;
                    }

                    if (State is ActivationState.Invalid && ForwardingAddress is { } forwardingAddress)
                    {
                        // This activation has forwarded its requests, so the cancellation follows them.
                        forwardTo = forwardingAddress;
                    }
                }
            }

            if (forwardTo is not null)
            {
                _shared.InternalRuntime.RuntimeClient.CancellationManager.SignalCancellation(forwardTo, GrainId, senderGrainId, messageId);
                return true;
            }

            var didCancel = false;
            if (message is not null && message.BodyObject is IInvokable request)
            {
                if (wasWaiting)
                {
                    // If the request was waiting, then we necessarily did manage to cancel it, so send the response now.
                    _shared.InternalRuntime.RuntimeClient.SendResponse(message, Response.FromException(new OperationCanceledException()));
                    didCancel = true;
                }
                else
                {
                    didCancel = TryCancelInvokable(request) || !request.IsCancellable;
                }
            }

            return didCancel;
        }

        bool TryCancelInvokable(IInvokable request)
        {
            try
            {
                return request.TryCancel();
            }
            catch (Exception exception)
            {
                LogErrorCancellationCallbackFailed(Shared.Logger, exception);
                return true;
            }
        }
    }

    /// <summary>
    /// Completes a request which was canceled while it was queued, without invoking it.
    /// </summary>
    private void CompleteCanceledRequest(Message message)
    {
        if (message.Direction is Message.Directions.Request)
        {
            try
            {
                _shared.InternalRuntime.RuntimeClient.SendResponse(message, Response.FromException(new OperationCanceledException()));
            }
            finally
            {
                // A request which this silo received still holds its serialized body. A request from a caller on this silo shares its body with the caller's pending call, so it is left intact.
                message.ReleaseBodyBuffer();
            }
        }
        else
        {
            message.Dispose();
        }
    }

    /// <summary>
    /// Applies the cancellations which this activation held while deactivating to the requests which it is about to forward or reject,
    /// then forwards the remaining cancellations to wherever its requests are now sent.
    /// </summary>
    /// <remarks>
    /// Called with the requests which deactivation removed from the queue, after this activation became invalid, so that it holds no further cancellations.
    /// Without a forwarding address, the remaining cancellations are dropped, as cancellations which do not find their request are on other activations.
    /// </remarks>
    private void ApplyHeldCancellations(HashSet<(GrainId SendingGrain, CorrelationId MessageId)> cancellations, List<Message>? requests)
    {
        if (requests is not null)
        {
            for (var i = requests.Count - 1; i >= 0; i--)
            {
                var request = requests[i];
                if (cancellations.Remove((request.SendingGrain, request.Id)))
                {
                    requests.RemoveAt(i);
                    CompleteCanceledRequest(request);
                }
            }
        }

        if (ForwardingAddress is { } forwardingAddress)
        {
            var cancellationManager = _shared.InternalRuntime.RuntimeClient.CancellationManager;
            foreach (var (sendingGrain, messageId) in cancellations)
            {
                cancellationManager.SignalCancellation(forwardingAddress, GrainId, sendingGrain, messageId);
            }
        }
    }

    private bool LockIsHeld()
    {
#if NET10_0_OR_GREATER
        return _lock.IsHeldByCurrentThread;
#else
        return Monitor.IsEntered(_lock);
#endif
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "One or more cancellation callbacks failed."
    )]
    private static partial void LogErrorCancellationCallbackFailed(ILogger logger, Exception exception);
}
