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

                if (message is null
                    && _extras is { } extras
                    && (extras.IsMigratingRequests || extras.HasMigratedRequests)
                    && ForwardingAddress is { } forwardingAddress)
                {
                    // Requests which migrated with this activation are canceled where they migrated.
                    // The new activation queues migrated requests before it activates, so a cancellation which reaches it then cancels the request before it runs.
                    // Only a migration source forwards cancellations. Its target holds a new activation, which a migration source cannot forward back to
                    // while this activation remains, so forwarding cannot cycle. Other activations, such as duplicates, retry briefly and then drop the cancellation.
                    forwardTo = forwardingAddress;
                    if (extras.IsMigratingRequests)
                    {
                        // The cancellation can reach the target before the migration does. It is kept until the migration's outcome is known,
                        // then applied here if the requests stay, or sent again if they migrated.
                        (_extras.InTransitCancellations ??= []).Add((senderGrainId, messageId));
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
    /// Completes a request which was canceled while it was in transit with this activation's migration, without invoking it.
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
    /// Ends the migration of this activation's queued requests, returning the cancellations which arrived while they were in transit.
    /// </summary>
    private HashSet<(GrainId SendingGrain, CorrelationId MessageId)>? EndInTransitCancellations()
    {
        Debug.Assert(LockIsHeld());
        var cancellations = _extras!.InTransitCancellations;
        _extras.InTransitCancellations = null;
        _extras.IsMigratingRequests = false;
        return cancellations;
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
