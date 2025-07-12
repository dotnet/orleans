#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime
{
    internal sealed partial class CallbackData
    {
        private readonly SharedCallbackData shared;
        private readonly IResponseCompletionSource context;
        private int completed;
        private StatusResponse? lastKnownStatus;
        private ValueStopwatch stopwatch;
        private CancellationTokenRegistration _cancellationTokenRegistration;

        public CallbackData(
            SharedCallbackData shared,
            IResponseCompletionSource ctx,
            Message msg)
        {
            this.shared = shared;
            this.context = ctx;
            this.Message = msg;
            this.stopwatch = ValueStopwatch.StartNew();
        }

        public Message Message { get; } // might hold metadata used by response pipeline

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

        private void SignalCancellation() => shared.CancellationManager?.SignalCancellation(Message.TargetSilo, Message.TargetGrain, Message.SendingGrain, Message.Id);

        public void OnStatusUpdate(StatusResponse status)
        {
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
            shared.Unregister(Message);
            ApplicationRequestInstruments.OnAppRequestsEnd((long)stopwatch.Elapsed.TotalMilliseconds);
            ApplicationRequestInstruments.OnAppRequestsTimedOut();
            OrleansCallBackDataEvent.Log.OnCanceled(Message);
            context.Complete(Response.FromException(new OperationCanceledException(_cancellationTokenRegistration.Token)));
            _cancellationTokenRegistration.Dispose();
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

            this.shared.Unregister(this.Message);
            _cancellationTokenRegistration.Dispose();
            ApplicationRequestInstruments.OnAppRequestsEnd((long)this.stopwatch.Elapsed.TotalMilliseconds);
            ApplicationRequestInstruments.OnAppRequestsTimedOut();

            OrleansCallBackDataEvent.Log.OnTimeout(this.Message);

            var msg = this.Message; // Local working copy

            var statusMessage = lastKnownStatus is StatusResponse status ? $"Last known status is {status}. " : string.Empty;
            var timeout = GetResponseTimeout();
            LogTimeout(this.shared.Logger, timeout, msg, statusMessage);

            var exception = new TimeoutException($"Response did not arrive on time in {timeout} for message: {msg}. {statusMessage}");
            context.Complete(Response.FromException(exception));
        }

        public void OnTargetSiloFail()
        {
            if (Interlocked.CompareExchange(ref this.completed, 1, 0) != 0)
            {
                return;
            }

            this.stopwatch.Stop();
            this.shared.Unregister(this.Message);
            _cancellationTokenRegistration.Dispose();
            ApplicationRequestInstruments.OnAppRequestsEnd((long)this.stopwatch.Elapsed.TotalMilliseconds);

            OrleansCallBackDataEvent.Log.OnTargetSiloFail(this.Message);
            var msg = this.Message;
            var statusMessage = lastKnownStatus is StatusResponse status ? $"Last known status is {status}. " : string.Empty;
            LogTargetSiloFail(this.shared.Logger, msg, statusMessage, Constants.TroubleshootingHelpLink);
            var exception = new SiloUnavailableException($"The target silo became unavailable for message: {msg}. {statusMessage}See {Constants.TroubleshootingHelpLink} for troubleshooting help.");
            this.context.Complete(Response.FromException(exception));
        }

        public void DoCallback(Message response)
        {
            if (Interlocked.CompareExchange(ref this.completed, 1, 0) != 0)
            {
                return;
            }

            OrleansCallBackDataEvent.Log.DoCallback(this.Message);

            this.stopwatch.Stop();
            _cancellationTokenRegistration.Dispose();
            ApplicationRequestInstruments.OnAppRequestsEnd((long)this.stopwatch.Elapsed.TotalMilliseconds);

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
}
