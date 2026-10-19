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
            var sendAsRequest = false;
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

                // An activation which is not processing requests may not hold the request yet, or may have sent it elsewhere, such as with its migration.
                // The cancellation is then sent to the grain as a request, which an activation holds and forwards as it does any other request until it can process it.
                sendAsRequest = message is null && State is not ActivationState.Valid;
            }

            if (sendAsRequest)
            {
                // This activation's migration may own the request. Otherwise, the cancellation follows the request as a request.
                if (Migration?.TryCancel(senderGrainId, messageId) is not true)
                {
                    _shared.InternalRuntime.RuntimeClient.CancellationManager.SignalCancellation(targetSilo: null, GrainId, senderGrainId, messageId);
                }

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

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "One or more cancellation callbacks failed."
    )]
    private static partial void LogErrorCancellationCallbackFailed(ILogger logger, Exception exception);
}
