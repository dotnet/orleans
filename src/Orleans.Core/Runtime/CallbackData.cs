using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime
{
    internal sealed partial class CallbackData
    {
        private const int StateNone = 0;
        private const int StateCompleted = 1;
        private const int StateCancellationRegistrationPending = 2;
        private const int StateCancellationRegistrationPublished = 4;
        private const int StateCancellationRequested = 8;

        private readonly SharedCallbackData shared;
        private readonly IResponseCompletionSource context;

        // The request's invocation describes its cancellation and response timeout. The message's body can change after it is sent:
        // a retried message holds its serialized body, which is released once the retry is sent.
        private readonly IInvokable? _invokable;
        private readonly long _startTimestamp;
        private int _state;
        private StatusResponse? lastKnownStatus;
        private CancellationTokenRegistration _cancellationTokenRegistration;

        public CallbackData(
            SharedCallbackData shared,
            IResponseCompletionSource ctx,
            Message msg)
        {
            this.shared = shared;
            this.context = ctx;
            this.Message = msg;
            _invokable = msg.BodyObject as IInvokable;
            _startTimestamp = shared.TimeProvider.GetTimestamp();
        }

        public Message Message { get; } // might hold metadata used by response pipeline

        public bool IsCompleted => (Volatile.Read(ref _state) & StateCompleted) != 0;

        public void SubscribeForCancellation(CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
            {
                return;
            }

            if (!TryUpdateState(
                blockingFlags: StateCompleted | StateCancellationRegistrationPending | StateCancellationRegistrationPublished,
                clearFlags: StateNone,
                setFlags: StateCancellationRegistrationPending))
            {
                return;
            }

            var registration = cancellationToken.UnsafeRegister(static (arg, token) =>
            {
                var callbackData = (CallbackData)arg!;
                callbackData.OnCancellation(token);
            }, this);

            _cancellationTokenRegistration = registration;
            if (!TryUpdateState(
                blockingFlags: StateCompleted,
                clearFlags: StateCancellationRegistrationPending,
                setFlags: StateCancellationRegistrationPublished))
            {
                registration.Dispose();
            }
        }

        private bool TryUpdateState(int blockingFlags, int clearFlags, int setFlags)
        {
            var state = Volatile.Read(ref _state);
            while ((state & blockingFlags) == 0)
            {
                var observed = Interlocked.CompareExchange(ref _state, (state & ~clearFlags) | setFlags, state);
                if (observed == state)
                {
                    return true;
                }

                state = observed;
            }

            return false;
        }

        private void SignalCancellation()
        {
            // Only cancel requests which honor cancellation token.
            // Not all targets support IGrainCallCancellationExtension, so sending a cancellation in those cases could result in an error.
            // There are opportunities to cancel requests at the infrastructure layer which this will not exploit if the target method does not support cancellation.
            if (_invokable is { IsCancellable: true })
            {
                shared.CancellationManager?.SignalCancellation(Message.TargetSilo, Message.TargetGrain, Message.SendingGrain, Message.Id);
            }
        }

        public void OnStatusUpdate(StatusResponse status)
        {
            this.lastKnownStatus = status;
        }

        /// <summary>
        /// Moves this call's target to the silo which now owns the request, after that silo has accepted it.
        /// </summary>
        /// <param name="targetSilo">The silo which now hosts the request.</param>
        /// <param name="forwardCount">The request's forward count at <paramref name="targetSilo"/>, which orders retargets for the same call.</param>
        /// <param name="isDeadSilo">Determines whether a silo has been declared dead. The call fails if <paramref name="targetSilo"/> is dead.</param>
        /// <returns><see langword="true"/> if the call was retargeted; <see langword="false"/> if the retarget was superseded.</returns>
        /// <remarks>
        /// A retarget which races with completion has no effect on the call's outcome. It only propagates a requested cancellation to the new target.
        /// </remarks>
        public bool TryRetarget(SiloAddress targetSilo, int forwardCount, Func<SiloAddress, bool> isDeadSilo)
        {
            // The call owns its message, and only this class locks it, so the message serves as the lock without adding a field to every pending call.
            lock (Message)
            {
                if (forwardCount <= Message.ForwardCount)
                {
                    return false;
                }

                Message.ForwardCount = forwardCount;
                Message.TargetSilo = targetSilo;
            }

            // Publish the new target before observing cancellation, so either this or OnCancellation signals the new target.
            Interlocked.MemoryBarrier();
            if ((Volatile.Read(ref _state) & StateCancellationRequested) != 0)
            {
                SignalCancellation();
            }

            // The new target's failure sweep only completes calls which it observes targeting that silo.
            // Membership marks a silo dead before sweeping its calls, so either the sweep observes this retarget or this check observes the death.
            if (isDeadSilo(targetSilo))
            {
                OnTargetSiloFail(targetSilo);
            }

            return true;
        }

        public bool IsExpired(long currentTimestamp)
        {
            var duration = currentTimestamp - _startTimestamp;
            return duration > GetResponseTimeoutTimestampTicks();
        }

        private long GetResponseTimeoutTimestampTicks()
        {
            var defaultResponseTimeout = _invokable?.GetDefaultResponseTimeout();
            if (defaultResponseTimeout.HasValue)
            {
                return shared.GetTimestampTicks(defaultResponseTimeout.Value);
            }

            return shared.ResponseTimeoutTimestampTicks;
        }

        private TimeSpan GetResponseTimeout() => _invokable?.GetDefaultResponseTimeout() ?? shared.ResponseTimeout;

        private string GetTargetGrainType()
        {
            var type = Message.TargetGrain.Type;
            return type.IsDefault ? "unknown" : type.ToString()!;
        }

        private void OnCancellation(CancellationToken cancellationToken)
        {
            Interlocked.Or(ref _state, StateCancellationRequested);

            // If waiting for acknowledgement is enabled, simply signal to the remote grain that cancellation
            // is requested and return.
            if (shared.WaitForCancellationAcknowledgement)
            {
                SignalCancellation();
                return;
            }

            // Otherwise, cancel the request immediately, without waiting for the callee to acknowledge the
            // cancellation request. The callee will still be signaled.
            if (!TryComplete())
            {
                return;
            }

            RecordElapsedTime();
            SignalCancellation();
            shared.Unregister(this);
            shared.ApplicationRequests.OnAppRequestsCanceled(GetTargetGrainType());
            OrleansCallBackDataEvent.Instance.OnCanceled(Message);
            context.Complete(Response.FromException(new OperationCanceledException(cancellationToken)));
            DisposeCancellationRegistration();
        }

        public void OnTimeout()
        {
            if (!TryComplete())
            {
                return;
            }

            RecordElapsedTime();
            if (shared.CancelRequestOnTimeout)
            {
                // Record the request before signaling, so that a concurrent retarget also signals its new target.
                Interlocked.Or(ref _state, StateCancellationRequested);
                SignalCancellation();
            }

            this.shared.Unregister(this);
            DisposeCancellationRegistration();
            shared.ApplicationRequests.OnAppRequestsTimedOut(GetTargetGrainType());

            OrleansCallBackDataEvent.Instance.OnTimeout(this.Message);

            var msg = this.Message; // Local working copy

            var statusMessage = lastKnownStatus is StatusResponse status ? $"Last known status is {status}. " : string.Empty;
            var timeout = GetResponseTimeout();
            LogTimeout(this.shared.Logger, timeout, msg, statusMessage);

            var exception = new TimeoutException($"Response did not arrive on time in {timeout} for message: {msg}. {statusMessage}");
            context.Complete(Response.FromException(exception));
        }

        /// <summary>
        /// Fails the call because <paramref name="failedSilo"/> became unavailable, unless the call has since been retargeted to another silo.
        /// </summary>
        public void OnTargetSiloFail(SiloAddress failedSilo)
        {
            // Retargeting changes the target under this lock, so the call fails only if it still targets the failed silo.
            lock (Message)
            {
                if (!failedSilo.Equals(Message.TargetSilo) || !TryComplete())
                {
                    return;
                }
            }

            RecordElapsedTime();
            this.shared.Unregister(this);
            DisposeCancellationRegistration();

            OrleansCallBackDataEvent.Instance.OnTargetSiloFail(this.Message);
            var msg = this.Message;
            var statusMessage = lastKnownStatus is StatusResponse status ? $"Last known status is {status}. " : string.Empty;
            LogTargetSiloFail(this.shared.Logger, msg, statusMessage, Constants.TroubleshootingHelpLink);
            var exception = new SiloUnavailableException($"The target silo became unavailable for message: {msg}. {statusMessage}See {Constants.TroubleshootingHelpLink} for troubleshooting help.");
            this.context.Complete(Response.FromException(exception));
        }

        public void OnHostShutdown()
        {
            if (!TryComplete())
            {
                return;
            }

            RecordElapsedTime();
            this.shared.Unregister(this);
            DisposeCancellationRegistration();

            var msg = this.Message;
            var exception = new SiloUnavailableException($"The local Orleans host is shutting down and can no longer process the request: {msg}.");
            this.context.Complete(Response.FromException(exception));
        }

        public void DoCallback(Message response)
        {
            TryDoCallback(response);
        }

        internal bool TryDoCallback(Message response)
        {
            if (!TryComplete())
            {
                return false;
            }

            OrleansCallBackDataEvent.Instance.DoCallback(this.Message);

            RecordElapsedTime();
            DisposeCancellationRegistration();

            // do callback outside the CallbackData lock. Just not a good practice to hold a lock for this unrelated operation.
            ResponseCallback(response, this.context);
            return true;
        }

        private bool TryComplete() => (Interlocked.Or(ref _state, StateCompleted) & StateCompleted) == 0;

        private void RecordElapsedTime()
        {
            if (shared.ApplicationRequests.AppRequestsLatencyEnabled)
            {
                var elapsedMilliseconds = shared.TimeProvider.GetElapsedTime(_startTimestamp).TotalMilliseconds;
                shared.ApplicationRequests.OnAppRequestsEnd(elapsedMilliseconds);
            }
        }

        private void DisposeCancellationRegistration()
        {
            // If registration is still pending, its publisher observes completion and disposes it.
            if ((Volatile.Read(ref _state) & StateCancellationRegistrationPublished) != 0)
            {
                _cancellationTokenRegistration.Dispose();
            }
        }

        private static void ResponseCallback(Message message, IResponseCompletionSource context)
        {
            try
            {
                var body = message.BodyObject;
                if (body is Response response)
                {
                    context.Complete(response);
                }
                else
                {
                    HandleRejectionResponse(context, body as RejectionResponse);
                }
            }
            catch (Exception exc)
            {
                // catch the exception and break the promise with it.
                context.Complete(Response.FromException(exc));
            }

            static void HandleRejectionResponse(IResponseCompletionSource context, RejectionResponse? rejection)
            {
                Exception exception;
                if (rejection?.RejectionType is Message.RejectionTypes.GatewayTooBusy)
                {
                    exception = new GatewayTooBusyException();
                }
                else
                {
                    exception = rejection?.Exception ?? new OrleansMessageRejectionException(rejection?.RejectionInfo ?? "Unable to send request - no rejection info available");
                }

                context.Complete(Response.FromException(exception));
            }
        }

        [LoggerMessage(
            EventId = (int)ErrorCode.Runtime_Error_100157,
            Level = LogLevel.Warning,
            Message = "Response did not arrive on time in '{Timeout}' for message: '{Message}'. {StatusMessage}About to break its promise."
        )]
        private static partial void LogTimeout(ILogger logger, TimeSpan timeout, Message message, string statusMessage);

        [LoggerMessage(
            EventId = (int)ErrorCode.Runtime_Error_100157,
            Level = LogLevel.Warning,
            Message = "The target silo became unavailable for message: '{Message}'. {StatusMessage}See {TroubleshootingHelpLink} for troubleshooting help. About to break its promise."
        )]
        private static partial void LogTargetSiloFail(ILogger logger, Message message, string statusMessage, string troubleshootingHelpLink);
    }
}
