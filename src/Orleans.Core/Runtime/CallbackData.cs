using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

        private SharedCallbackData shared = null!;
        private IResponseCompletionSource context = null!;
        private ApplicationRequestInstruments _applicationRequestInstruments = null!;
        private int _state;
        private StatusResponse? lastKnownStatus;
        private ValueStopwatch stopwatch;
        private CancellationTokenRegistration _cancellationTokenRegistration;
        private int _refCount;
        private long _generation;

        internal CallbackData()
        {
        }

        internal void Initialize(SharedCallbackData shared, IResponseCompletionSource ctx, Message msg, ApplicationRequestInstruments applicationRequestInstruments)
        {
            Debug.Assert(_refCount == 0, "CallbackData ref count should be 0 before initialization");
            this.shared = shared;
            this.context = ctx;
            this.Message = msg;
            _applicationRequestInstruments = applicationRequestInstruments;
            this.stopwatch = ValueStopwatch.StartNew();
        }

        internal void Reset()
        {
            Debug.Assert(_refCount == 0, "CallbackData ref count should be 0 before reset");
            shared = null!;
            context = null!;
            _applicationRequestInstruments = null!;
            _state = StateNone;
            lastKnownStatus = null;
            stopwatch = default;
            _cancellationTokenRegistration.Dispose();
            _cancellationTokenRegistration = default;
            Message = null!;
        }

        internal long AcquireOwnerReference()
        {
            var generation = Interlocked.Increment(ref _generation);
            if (Interlocked.CompareExchange(ref _refCount, 1, 0) != 0)
            {
                throw new InvalidOperationException("CallbackData already has an owner.");
            }

            return generation;
        }

        internal bool TryAcquireLease(long generation)
        {
            while (true)
            {
                if (generation != Volatile.Read(ref _generation))
                {
                    return false;
                }

                var currentRefCount = Volatile.Read(ref _refCount);

                if (currentRefCount <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _refCount, currentRefCount + 1, currentRefCount) == currentRefCount)
                {
                    if (generation == Volatile.Read(ref _generation))
                    {
                        return true;
                    }

                    ReleaseReference();
                    return false;
                }
            }
        }

        internal void ReleaseLease(long generation)
        {
            if (generation != Volatile.Read(ref _generation))
            {
                throw new InvalidOperationException("Cannot release a stale CallbackData reference.");
            }

            ReleaseReference();
        }

        private void ReleaseReference()
        {
            var newRefCount = Interlocked.Decrement(ref _refCount);
            if (newRefCount == 0)
            {
                CallbackDataPool.ReturnCore(this);
            }
            else if (newRefCount < 0)
            {
                Debug.Fail("CallbackData ref count went negative");
                throw new InvalidOperationException("CallbackData was released more than once.");
            }
        }

        public Message Message { get; private set; } = null!; // might hold metadata used by response pipeline

        public bool IsCompleted => (Volatile.Read(ref _state) & StateCompleted) != 0;

        public void SubscribeForCancellation(CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                ref _state,
                StateCancellationRegistrationPending,
                StateNone) != StateNone)
            {
                return;
            }

            var registration = cancellationToken.UnsafeRegister(static (arg, token) =>
            {
                var callbackData = (CallbackData)arg!;
                callbackData.OnCancellation(token);
            }, this);

            _cancellationTokenRegistration = registration;
            if (Interlocked.CompareExchange(
                ref _state,
                StateCancellationRegistrationPublished,
                StateCancellationRegistrationPending) != StateCancellationRegistrationPending)
            {
                registration.Dispose();
            }
        }

        private void SignalCancellation()
        {
            // Only cancel requests which honor cancellation token.
            // Not all targets support IGrainCallCancellationExtension, so sending a cancellation in those cases could result in an error.
            // There are opportunities to cancel requests at the infrastructure layer which this will not exploit if the target method does not support cancellation.
            if (Message.BodyObject is IInvokable invokable && invokable.IsCancellable)
            {
                shared.CancellationManager?.SignalCancellation(Message.TargetSilo, Message.TargetGrain, Message.SendingGrain, Message.Id);
            }
        }

        public void OnStatusUpdate(StatusResponse status) => this.lastKnownStatus = status;

        public bool IsExpired(long currentTimestamp)
        {
            var duration = currentTimestamp - this.stopwatch.GetRawTimestamp();
            return duration > GetResponseTimeoutStopwatchTicks();
        }

        private long GetResponseTimeoutStopwatchTicks()
        {
            var defaultResponseTimeout = (Message.BodyObject as IInvokable)?.GetDefaultResponseTimeout();
            if (defaultResponseTimeout.HasValue)
            {
                return (long)(defaultResponseTimeout.Value.TotalSeconds * Stopwatch.Frequency);
            }

            return shared.ResponseTimeoutStopwatchTicks;
        }

        private TimeSpan GetResponseTimeout() => (Message.BodyObject as IInvokable)?.GetDefaultResponseTimeout() ?? shared.ResponseTimeout;

        private string GetTargetGrainType()
        {
            var type = Message.TargetGrain.Type;
            return type.IsDefault ? "unknown" : type.ToString()!;
        }

        private void OnCancellation(CancellationToken cancellationToken)
        {
            var generation = Volatile.Read(ref _generation);
            if (!TryAcquireLease(generation))
            {
                return;
            }

            try
            {
                OnCancellationCore(cancellationToken);
            }
            finally
            {
                ReleaseLease(generation);
            }
        }

        private void OnCancellationCore(CancellationToken cancellationToken)
        {
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

            stopwatch.Stop();
            SignalCancellation();
            shared.Unregister(Message);
            DisposeCancellationRegistration();
            _applicationRequestInstruments.OnAppRequestsEnd((long)stopwatch.Elapsed.TotalMilliseconds);
            _applicationRequestInstruments.OnAppRequestsCanceled(GetTargetGrainType());
            OrleansCallBackDataEvent.Instance.OnCanceled(Message);
            context.Complete(Response.FromException(new OperationCanceledException(cancellationToken)));
        }

        public void OnTimeout()
        {
            if (!TryComplete())
            {
                return;
            }

            this.stopwatch.Stop();
            if (shared.CancelRequestOnTimeout)
            {
                SignalCancellation();
            }

            this.shared.Unregister(this.Message);
            DisposeCancellationRegistration();
            _applicationRequestInstruments.OnAppRequestsEnd((long)this.stopwatch.Elapsed.TotalMilliseconds);
            _applicationRequestInstruments.OnAppRequestsTimedOut(GetTargetGrainType());

            OrleansCallBackDataEvent.Instance.OnTimeout(this.Message);
            var msg = this.Message;
            var statusMessage = lastKnownStatus is StatusResponse status ? $"Last known status is {status}. " : string.Empty;
            var timeout = GetResponseTimeout();
            LogTimeout(this.shared.Logger, timeout, msg, statusMessage);
            var exception = new TimeoutException($"Response did not arrive on time in {timeout} for message: {msg}. {statusMessage}");
            context.Complete(Response.FromException(exception));
        }

        public void OnTargetSiloFail()
        {
            if (!TryComplete())
            {
                return;
            }

            this.stopwatch.Stop();
            this.shared.Unregister(this.Message);
            DisposeCancellationRegistration();
            _applicationRequestInstruments.OnAppRequestsEnd((long)this.stopwatch.Elapsed.TotalMilliseconds);

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

            this.stopwatch.Stop();
            this.shared.Unregister(this.Message);
            DisposeCancellationRegistration();
            _applicationRequestInstruments.OnAppRequestsEnd((long)this.stopwatch.Elapsed.TotalMilliseconds);

            var msg = this.Message;
            var exception = new SiloUnavailableException($"The local Orleans host is shutting down and can no longer process the request: {msg}.");
            this.context.Complete(Response.FromException(exception));
        }

        public void DoCallback(Message response)
        {
            if (!TryComplete())
            {
                return;
            }

            OrleansCallBackDataEvent.Instance.DoCallback(this.Message);

            this.stopwatch.Stop();
            DisposeCancellationRegistration();
            _applicationRequestInstruments.OnAppRequestsEnd((long)this.stopwatch.Elapsed.TotalMilliseconds);

            // do callback outside the CallbackData lock. Just not a good practice to hold a lock for this unrelated operation.
            ResponseCallback(response, this.context);
        }

        private bool TryComplete() => (Interlocked.Or(ref _state, StateCompleted) & StateCompleted) == 0;

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

    /// <summary>
    /// Holds the dictionary-owned reference to a pooled <see cref="CallbackData"/> instance.
    /// </summary>
    internal readonly struct CallbackDataOwner
    {
        private readonly CallbackData? _callback;
        private readonly long _generation;

        public CallbackDataOwner(CallbackData callback)
        {
            _callback = callback;
            _generation = callback.AcquireOwnerReference();
        }

        public CallbackDataLease Acquire()
        {
            var callback = _callback ?? throw new InvalidOperationException("CallbackDataOwner is not initialized.");
            if (callback.TryAcquireLease(_generation))
            {
                return new CallbackDataLease(callback, _generation);
            }

            return default;
        }

        public void Release()
        {
            var callback = _callback ?? throw new InvalidOperationException("CallbackDataOwner is not initialized.");
            callback.ReleaseLease(_generation);
        }
    }

    /// <summary>
    /// Holds a scoped reference to a pooled <see cref="CallbackData"/> instance.
    /// </summary>
    internal ref struct CallbackDataLease
    {
        private CallbackData? _callback;
        private readonly long _generation;

        internal CallbackDataLease(CallbackData callback, long generation)
        {
            _callback = callback;
            _generation = generation;
        }

        public readonly CallbackData Value =>
            _callback ?? throw new InvalidOperationException("CallbackDataLease is not initialized.");

        public readonly bool TryGetValue([NotNullWhen(true)] out CallbackData? callback)
        {
            callback = _callback;
            return callback is not null;
        }

        public void Dispose()
        {
            if (_callback is { } callback)
            {
                _callback = null;
                callback.ReleaseLease(_generation);
            }
        }
    }
}
