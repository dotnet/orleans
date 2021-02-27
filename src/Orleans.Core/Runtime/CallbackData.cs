using System;
using System.Threading;
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
            this.context = ctx;
            this.Message = msg;
            this.stopwatch = ValueStopwatch.StartNew();
        }

        public Message Message { get; } // might hold metadata used by response pipeline

        public bool IsCompleted => this.completed == 1;

        public void OnStatusUpdate(StatusResponse status)
        {
            this.lastKnownStatus = status;
        }
        
        public bool IsExpired(long currentTimestamp)
        {
            var duration = currentTimestamp - this.stopwatch.GetRawTimestamp();
            return duration > shared.ResponseTimeoutStopwatchTicks;
        }

        public void OnTimeout(TimeSpan timeout)
        {
            if (Interlocked.CompareExchange(ref completed, 1, 0) != 0)
            {
                return;
            }

            this.shared.Unregister(this.Message);

            var requestStatistics = this.shared.RequestStatistics;
            if (requestStatistics.CollectApplicationRequestsStats)
            {
                this.stopwatch.Stop();
                requestStatistics.OnAppRequestsEnd(this.stopwatch.Elapsed);
                requestStatistics.OnAppRequestsTimedOut();
            }

            OrleansCallBackDataEvent.Log.OnTimeout(this.Message);

            var msg = this.Message; // Local working copy

            string messageHistory = msg.GetTargetHistory();
            var statusMessage = lastKnownStatus is StatusResponse status ? $"Last known status is {status}. " : string.Empty;
            string errorMsg = $"Response did not arrive on time in {timeout} for message: {msg}. {statusMessage}Target History is: {messageHistory}.";
            this.shared.Logger.Warn(ErrorCode.Runtime_Error_100157, "{0} About to break its promise.", errorMsg);

            var error = Message.CreatePromptExceptionResponse(msg, new TimeoutException(errorMsg));
            ResponseCallback(error, this.context);
            //(this.Message.BodyObject as IDisposable)?.Dispose();
        }

        public void OnTargetSiloFail()
        {
            if (Interlocked.CompareExchange(ref this.completed, 1, 0) != 0)
            {
                return;
            }

            this.shared.Unregister(this.Message);
            var requestStatistics = this.shared.RequestStatistics;
            if (requestStatistics.CollectApplicationRequestsStats)
            {
                this.stopwatch.Stop();
                requestStatistics.OnAppRequestsEnd(this.stopwatch.Elapsed);
            }

            OrleansCallBackDataEvent.Log.OnTargetSiloFail(this.Message);
            var msg = this.Message;
            var messageHistory = msg.GetTargetHistory();
            var statusMessage = lastKnownStatus is StatusResponse status ? $"Last known status is {status}. " : string.Empty;
            var errorMsg =
                $"The target silo became unavailable for message: {msg}. {statusMessage}Target History is: {messageHistory}. See {Constants.TroubleshootingHelpLink} for troubleshooting help.";
            this.shared.Logger.Warn(ErrorCode.Runtime_Error_100157, "{0} About to break its promise.", errorMsg);
            var error = Message.CreatePromptExceptionResponse(msg, new SiloUnavailableException(errorMsg));
            ResponseCallback(error, this.context);
            //(this.Message.BodyObject as IDisposable)?.Dispose();
        }

        public void DoCallback(Message response)
        {
            if (Interlocked.CompareExchange(ref this.completed, 1, 0) != 0)
            {
                return;
            }

            OrleansCallBackDataEvent.Log.DoCallback(this.Message);

            var requestStatistics = this.shared.RequestStatistics;
            if (requestStatistics.CollectApplicationRequestsStats)
            {
                this.stopwatch.Stop();
                requestStatistics.OnAppRequestsEnd(this.stopwatch.Elapsed);
            }

            // do callback outside the CallbackData lock. Just not a good practice to hold a lock for this unrelated operation.
            ResponseCallback(response, this.context);
            //(this.Message.BodyObject as IDisposable)?.Dispose();
        }

        public static void ResponseCallback(Message message, IResponseCompletionSource context)
        {
            if (message.Result != Message.ResponseTypes.Rejection)
            {
                try
                {
                    var response = (Response)message.BodyObject;
                    context.Complete(response);
                }
                catch (Exception exc)
                {
                    // catch the exception and break the promise with it.
                    context.Complete(Response.FromException(exc));
                }
            }
            else
            {
                OnRejection(message, context);
            }
        }

        private static void OnRejection(Message message, IResponseCompletionSource context)
        {
            Exception rejection;
            switch (message.RejectionType)
            {
                case Message.RejectionTypes.GatewayTooBusy:
                    rejection = new GatewayTooBusyException();
                    break;
                case Message.RejectionTypes.DuplicateRequest:
                    return; // Ignore duplicates

                default:
                    rejection = message.BodyObject as Exception;
                    if (rejection == null)
                    {
                        if (string.IsNullOrEmpty(message.RejectionInfo))
                        {
                            message.RejectionInfo = "Unable to send request - no rejection info available";
                        }
                        rejection = new OrleansMessageRejectionException(message.RejectionInfo);
                    }
                    break;
            }

            context.Complete(Response.FromException(rejection));
        }
    }
}
