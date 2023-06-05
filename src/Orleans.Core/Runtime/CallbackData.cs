using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime
{
    internal class CallbackData
    {
        private readonly SharedCallbackData shared;
        private readonly IResponseCompletionSource context;
        private int completed;
        private StatusResponse lastKnownStatus;
        private ValueStopwatch stopwatch;

        public CallbackData(
            SharedCallbackData shared,
            IResponseCompletionSource ctx,
            Message msg)
        {
            this.shared = shared;
            context = ctx;
            Message = msg;
            stopwatch = ValueStopwatch.StartNew();
        }

        public Message Message { get; } // might hold metadata used by response pipeline

        public bool IsCompleted => completed == 1;

        public void OnStatusUpdate(StatusResponse status)
        {
            lastKnownStatus = status;
        }

        public bool IsExpired(long currentTimestamp)
        {
            var duration = currentTimestamp - stopwatch.GetRawTimestamp();
            return duration > shared.ResponseTimeoutStopwatchTicks;
        }

        public void OnTimeout(TimeSpan timeout)
        {
            if (Interlocked.CompareExchange(ref completed, 1, 0) != 0)
            {
                return;
            }

            shared.Unregister(Message);

            stopwatch.Stop();
            ApplicationRequestInstruments.OnAppRequestsEnd((long)stopwatch.Elapsed.TotalMilliseconds);
            ApplicationRequestInstruments.OnAppRequestsTimedOut();

            OrleansCallBackDataEvent.Log.OnTimeout(Message);

            var msg = Message; // Local working copy

            var statusMessage = lastKnownStatus is StatusResponse status ? $"Last known status is {status}. " : string.Empty;
            shared.Logger.LogWarning(
                (int)ErrorCode.Runtime_Error_100157,
                "Response did not arrive on time in {Timeout} for message: {Message}. {StatusMessage}. About to break its promise.",
                timeout,
                msg,
                statusMessage);

            var exception = new TimeoutException($"Response did not arrive on time in {timeout} for message: {msg}. {statusMessage}");
            context.Complete(Response.FromException(exception));
        }

        public void OnTargetSiloFail()
        {
            if (Interlocked.CompareExchange(ref completed, 1, 0) != 0)
            {
                return;
            }

            shared.Unregister(Message);
            stopwatch.Stop();
            ApplicationRequestInstruments.OnAppRequestsEnd((long)stopwatch.Elapsed.TotalMilliseconds);

            OrleansCallBackDataEvent.Log.OnTargetSiloFail(Message);
            var msg = Message;
            var statusMessage = lastKnownStatus is StatusResponse status ? $"Last known status is {status}. " : string.Empty;
            shared.Logger.LogWarning(
                (int)ErrorCode.Runtime_Error_100157,
                "The target silo became unavailable for message: {Message}. {StatusMessage}See {TroubleshootingHelpLink} for troubleshooting help. About to break its promise.",
                msg,
                statusMessage,
                Constants.TroubleshootingHelpLink);
            var exception = new SiloUnavailableException($"The target silo became unavailable for message: {msg}. {statusMessage}See {Constants.TroubleshootingHelpLink} for troubleshooting help.");
            context.Complete(Response.FromException(exception));
        }

        public void DoCallback(Message response)
        {
            if (Interlocked.CompareExchange(ref completed, 1, 0) != 0)
            {
                return;
            }

            OrleansCallBackDataEvent.Log.DoCallback(Message);

            stopwatch.Stop();
            ApplicationRequestInstruments.OnAppRequestsEnd((long)stopwatch.Elapsed.TotalMilliseconds);

            // do callback outside the CallbackData lock. Just not a good practice to hold a lock for this unrelated operation.
            ResponseCallback(response, context);
        }

        public static void ResponseCallback(Message message, IResponseCompletionSource context)
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

            static void HandleRejectionResponse(IResponseCompletionSource context, RejectionResponse rejection)
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
    }
}
