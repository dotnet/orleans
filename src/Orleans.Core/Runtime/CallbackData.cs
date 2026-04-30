using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime
{
    internal sealed partial class CallbackData
    {
        private SharedCallbackData shared = null!;
        private IResponseCompletionSource context = null!;
        private ApplicationRequestInstruments _applicationRequestInstruments = null!;
        private int completed;
        private StatusResponse? lastKnownStatus;
        private ValueStopwatch stopwatch;
        private CancellationTokenRegistration _cancellationTokenRegistration;
        private CorrelationId _correlationId;
        private int _refCount;

        /// <summary>
        /// Parameterless constructor for object pooling.
        /// </summary>
        internal CallbackData()
        {
        }

        public CallbackData(
            SharedCallbackData shared,
            IResponseCompletionSource ctx,
            Message msg,
            ApplicationRequestInstruments applicationRequestInstruments)
        {
            Initialize(shared, ctx, msg, applicationRequestInstruments);
        }

        /// <summary>
        /// Initializes the callback data for use. Called after retrieving from the pool.
        /// Does NOT set ref count - that is done by <see cref="CallbackDataOwner"/> constructor.
        /// </summary>
        public void Initialize(SharedCallbackData shared, IResponseCompletionSource ctx, Message msg, ApplicationRequestInstruments applicationRequestInstruments)
        {
            Debug.Assert(_refCount == 0, "CallbackData ref count should be 0 before initialization");
            this.shared = shared;
            this.context = ctx;
            this.Message = msg;
            _applicationRequestInstruments = applicationRequestInstruments;
            this._correlationId = msg.Id;
            this.stopwatch = ValueStopwatch.StartNew();
        }

        /// <summary>
        /// Resets the callback data for return to the pool.
        /// </summary>
        internal void Reset()
        {
            Debug.Assert(_refCount == 0, "CallbackData ref count should be 0 before reset");
            shared = null!;
            context = null!;
            _applicationRequestInstruments = null!;
            completed = 0;
            lastKnownStatus = null;
            stopwatch = default;
            _cancellationTokenRegistration.Dispose();
            _cancellationTokenRegistration = default;
            _correlationId = default;
            Message = null!;
        }

        /// <summary>
        /// Acquires the initial owner reference. Must only be called once after initialization.
        /// </summary>
        /// <returns>The previous ref count (should be 0).</returns>
        internal int AcquireOwnerReference()
        {
            return Interlocked.Increment(ref _refCount) - 1;
        }

        /// <summary>
        /// Attempts to acquire a borrowed lease on this callback, incrementing the ref count.
        /// Returns true only if the ref count is positive (object is still owned).
        /// </summary>
        /// <returns>True if the lease was acquired, false if the object is being/has been returned to pool.</returns>
        internal bool TryAcquireLease()
        {
            // Spin until we either successfully increment or detect ref count is 0
            while (true)
            {
                var currentRefCount = Volatile.Read(ref _refCount);

                // If ref count is 0 or negative, the object is being returned to pool or already pooled
                if (currentRefCount <= 0)
                {
                    return false;
                }

                // Try to increment the ref count
                if (Interlocked.CompareExchange(ref _refCount, currentRefCount + 1, currentRefCount) == currentRefCount)
                {
                    return true;
                }

                // CAS failed, spin and retry
            }
        }

        /// <summary>
        /// Releases a lease on this callback, decrementing the ref count.
        /// If the ref count reaches zero, returns the callback to the pool.
        /// </summary>
        internal void ReleaseLease()
        {
            var newRefCount = Interlocked.Decrement(ref _refCount);
            if (newRefCount == 0)
            {
                CallbackDataPool.ReturnCore(this);
            }
            else if (newRefCount < 0)
            {
                // This should never happen - indicates a bug
                Debug.Fail("CallbackData ref count went negative");
                throw new InvalidOperationException("CallbackData ref count went negative - indicates a double release bug");
            }
        }

        public Message Message { get; private set; } = null!; // might hold metadata used by response pipeline

        public bool IsCompleted => this.completed == 1;

        public void SubscribeForCancellation(CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _cancellationTokenRegistration = cancellationToken.UnsafeRegister(static arg =>
            {
                var callbackData = (CallbackData)arg!;
                callbackData.OnCancellation();
            }, this);
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

        public void OnStatusUpdate(CorrelationId messageId, StatusResponse status)
        {
            // Validate that the status update is for this callback instance.
            // This is necessary because the callback may have been returned to the pool
            // and reused for a different message between TryGetValue and OnStatusUpdate.
            if (_correlationId != messageId)
            {
                return;
            }

            this.lastKnownStatus = status;
        }

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

        private void OnCancellation()
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
            if (Interlocked.CompareExchange(ref completed, 1, 0) != 0)
            {
                return;
            }

            stopwatch.Stop();
            SignalCancellation();

            // Capture locals before Unregister, which may return this to the pool
            var elapsedMs = (long)stopwatch.Elapsed.TotalMilliseconds;
            var msg = Message;
            var ctx = context;
            var instruments = _applicationRequestInstruments;
            var cancellationToken = _cancellationTokenRegistration.Token;
            _cancellationTokenRegistration.Dispose();

            // Unregister last - this may return the callback to the pool
            shared.Unregister(msg);

            instruments.OnAppRequestsEnd(elapsedMs);
            instruments.OnAppRequestsCanceled();
            OrleansCallBackDataEvent.Instance.OnCanceled(msg);
            ctx.Complete(Response.FromException(new OperationCanceledException(cancellationToken)));
        }

        public void OnTimeout()
        {
            if (Interlocked.CompareExchange(ref completed, 1, 0) != 0)
            {
                return;
            }

            this.stopwatch.Stop();
            if (shared.CancelRequestOnTimeout)
            {
                SignalCancellation();
            }

            // Capture locals before Unregister, which may return this to the pool
            _cancellationTokenRegistration.Dispose();
            var elapsedMs = (long)this.stopwatch.Elapsed.TotalMilliseconds;
            var msg = this.Message;
            var statusMessage = lastKnownStatus is StatusResponse status ? $"Last known status is {status}. " : string.Empty;
            var timeout = GetResponseTimeout();
            var logger = this.shared.Logger;
            var ctx = this.context;
            var instruments = _applicationRequestInstruments;

            // Unregister last - this may return the callback to the pool
            this.shared.Unregister(msg);

            instruments.OnAppRequestsEnd(elapsedMs);
            instruments.OnAppRequestsTimedOut();

            OrleansCallBackDataEvent.Instance.OnTimeout(msg);

            LogTimeout(logger, timeout, msg, statusMessage);

            var exception = new TimeoutException($"Response did not arrive on time in {timeout} for message: {msg}. {statusMessage}");
            ctx.Complete(Response.FromException(exception));
        }

        public void OnTargetSiloFail()
        {
            if (Interlocked.CompareExchange(ref this.completed, 1, 0) != 0)
            {
                return;
            }

            this.stopwatch.Stop();

            // Capture locals before Unregister, which may return this to the pool
            _cancellationTokenRegistration.Dispose();
            var elapsedMs = (long)this.stopwatch.Elapsed.TotalMilliseconds;
            var msg = this.Message;
            var statusMessage = lastKnownStatus is StatusResponse status ? $"Last known status is {status}. " : string.Empty;
            var logger = this.shared.Logger;
            var ctx = this.context;
            var instruments = _applicationRequestInstruments;

            // Unregister last - this may return the callback to the pool
            this.shared.Unregister(msg);
            instruments.OnAppRequestsEnd(elapsedMs);

            OrleansCallBackDataEvent.Instance.OnTargetSiloFail(msg);
            LogTargetSiloFail(logger, msg, statusMessage, Constants.TroubleshootingHelpLink);
            var exception = new SiloUnavailableException($"The target silo became unavailable for message: {msg}. {statusMessage}See {Constants.TroubleshootingHelpLink} for troubleshooting help.");
            ctx.Complete(Response.FromException(exception));
        }

        public void DoCallback(Message response)
        {
            if (Interlocked.CompareExchange(ref this.completed, 1, 0) != 0)
            {
                return;
            }

            OrleansCallBackDataEvent.Instance.DoCallback(this.Message);

            this.stopwatch.Stop();
            _cancellationTokenRegistration.Dispose();
            _applicationRequestInstruments.OnAppRequestsEnd((long)this.stopwatch.Elapsed.TotalMilliseconds);

            // do callback outside the CallbackData lock. Just not a good practice to hold a lock for this unrelated operation.
            ResponseCallback(response, this.context);
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
    /// Owns a pooled <see cref="CallbackData"/> instance and manages its lifecycle.
    /// This represents the "owner" - the code that creates this and stores it in the callbacks dictionary.
    /// When the owner is done with the callback, it should call <see cref="Release"/> to return it to the pool.
    /// The owner increments the ref count to 1 on construction.
    /// </summary>
    internal readonly struct CallbackDataOwner
    {
        /// <summary>
        /// The callback data instance.
        /// </summary>
        public CallbackData Callback { get; }

        public CallbackDataOwner(CallbackData callback)
        {
            Debug.Assert(callback is not null, "CallbackDataOwner requires a non-null callback");
            Callback = callback;
            var previousRefCount = callback.AcquireOwnerReference();
            Debug.Assert(previousRefCount == 0, $"CallbackData ref count should have been 0 when creating owner, but was {previousRefCount}");
        }

        /// <summary>
        /// Attempts to acquire a borrowed lease on the callback.
        /// The returned lease MUST be disposed when done to release the reference count.
        /// Use <see cref="CallbackDataLease.TryGetValue"/> to check if the lease is valid and get the callback.
        /// </summary>
        /// <returns>A lease that must be disposed. Check <see cref="CallbackDataLease.TryGetValue"/> to see if it's valid.</returns>
        public CallbackDataLease Acquire()
        {
            Debug.Assert(Callback is not null, "CallbackDataOwner.Acquire called on default struct");
            if (Callback.TryAcquireLease())
            {
                return new CallbackDataLease(Callback);
            }

            return default;
        }

        /// <summary>
        /// Releases the owner's reference to the callback, potentially returning it to the pool.
        /// This should be called when the callback is removed from the dictionary and is no longer needed.
        /// </summary>
        public void Release()
        {
            Debug.Assert(Callback is not null, "CallbackDataOwner.Release called on default struct");
            Callback.ReleaseLease();
        }
    }

    /// <summary>
    /// A borrowed lease on a <see cref="CallbackData"/> instance.
    /// This is a ref struct to ensure it cannot escape the current scope without being disposed.
    /// Disposing the lease releases the reference count, potentially allowing the callback to be returned to the pool.
    /// </summary>
    internal ref struct CallbackDataLease
    {
        private CallbackData? _callback;

        internal CallbackDataLease(CallbackData callback)
        {
            _callback = callback;
        }

        /// <summary>
        /// Gets whether this lease is valid (successfully acquired a reference).
        /// </summary>
        public readonly bool IsValid => _callback is not null;

        /// <summary>
        /// Attempts to get the callback data if the lease is valid.
        /// </summary>
        /// <param name="callback">The callback data if valid, otherwise null.</param>
        /// <returns>True if the lease is valid and the callback was returned.</returns>
        public readonly bool TryGetValue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CallbackData? callback)
        {
            callback = _callback;
            return callback is not null;
        }

        /// <summary>
        /// Releases the lease, decrementing the reference count.
        /// If this was the last reference, the callback will be returned to the pool.
        /// </summary>
        public void Dispose()
        {
            if (_callback is { } callback)
            {
                _callback = null;
                callback.ReleaseLease();
            }
        }
    }
}
