using System;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.DistributedTracing.EventSourceEvents;
using Orleans.Transactions;

namespace Orleans.Runtime
{
    internal class CallbackData
    {
        private readonly SharedCallbackData shared;
        private readonly TaskCompletionSource<object> context;
        private int completed;
        private ValueStopwatch stopwatch;

        public CallbackData(
            SharedCallbackData shared,
            TaskCompletionSource<object> ctx, 
            Message msg)
        {
            this.shared = shared;
            this.context = ctx;
            this.Message = msg;
            this.TransactionInfo = TransactionContext.GetTransactionInfo();
            this.stopwatch = ValueStopwatch.StartNew();
        }

        public ITransactionInfo TransactionInfo { get; set; }

        public Message Message { get; set; } // might hold metadata used by response pipeline

        public bool IsCompleted => this.completed == 1;

        public bool IsExpired(long currentTimestamp)
        {
            return currentTimestamp - this.stopwatch.GetRawTimestamp() > this.shared.ResponseTimeoutStopwatchTicks;
        }

        public void OnTimeout(TimeSpan timeout)
        {
            // set/clear activityid
            if (this.IsCompleted)
                return;
            EventSource.SetCurrentThreadActivityId(this.Message.ActivityId, out var previousActivityId);
            try
            {
                OrleansCallBackDataEvent.Log.OnTimeoutStart();
               
                var msg = this.Message; // Local working copy

                string messageHistory = msg.GetTargetHistory();
                string errorMsg =
                    $"Response did not arrive on time in {timeout} for message: {msg}. Target History is: {messageHistory}.";
                this.shared.Logger.Warn(ErrorCode.Runtime_Error_100157, "{0} About to break its promise.", errorMsg);

                var error = Message.CreatePromptExceptionResponse(msg, new TimeoutException(errorMsg));
                OnFail(msg, error, "OnTimeout - Resend {0} for {1}", true);
            }
            finally
            {
                OrleansCallBackDataEvent.Log.OnTimeoutStop();
                EventSource.SetCurrentThreadActivityId(previousActivityId);
            }

        }

        public void OnTargetSiloFail()
        {
            if (this.IsCompleted)
                return;
            EventSource.SetCurrentThreadActivityId(this.Message.ActivityId, out var previousActivityId);
            try
            {
                OrleansCallBackDataEvent.Log.OnTargetSiloFailStart();
                var msg = this.Message;
                var messageHistory = msg.GetTargetHistory();
                string errorMsg =
                    $"The target silo became unavailable for message: {msg}. Target History is: {messageHistory}. See {Constants.TroubleshootingHelpLink} for troubleshooting help.";
                this.shared.Logger.Warn(ErrorCode.Runtime_Error_100157, "{0} About to break its promise.", errorMsg);

                var error = Message.CreatePromptExceptionResponse(msg, new SiloUnavailableException(errorMsg));
                OnFail(msg, error, "On silo fail - Resend {0} for {1}");
            }
            finally
            {
                OrleansCallBackDataEvent.Log.OnTargetSiloFailStop();
                EventSource.SetCurrentThreadActivityId(previousActivityId);
            }
            
        }

        public void DoCallback(Message response)
        {
            if (this.IsCompleted)
                return;
            EventSource.SetCurrentThreadActivityId(this.Message.ActivityId, out var previousActivityId);
            try
            {
                OrleansCallBackDataEvent.Log.DoCallbackStart();

                if (Interlocked.CompareExchange(ref this.completed, 1, 0) == 0)
                {
                    var requestStatistics = this.shared.RequestStatistics;
                    if (response.Result == Message.ResponseTypes.Rejection && response.RejectionType == Message.RejectionTypes.Transient)
                    {
                        if (this.shared.ShouldResend(this.Message))
                        {
                            return;
                        }
                    }

                    if (requestStatistics.CollectApplicationRequestsStats)
                    {
                        this.stopwatch.Stop();
                    }

                    if (requestStatistics.CollectApplicationRequestsStats)
                    {
                        requestStatistics.OnAppRequestsEnd(this.stopwatch.Elapsed);
                    }

                    // do callback outside the CallbackData lock. Just not a good practice to hold a lock for this unrelated operation.
                    this.shared.ResponseCallback(response, this.context);
                }
            }
            finally
            {
                OrleansCallBackDataEvent.Log.DoCallbackStop();
                EventSource.SetCurrentThreadActivityId(previousActivityId);
            }
            
        }

        private void OnFail(Message msg, Message error, string resendLogMessageFormat, bool isOnTimeout = false)
        {
            if (Interlocked.CompareExchange(ref this.completed, 1, 0) == 0)
            {
                var requestStatistics = this.shared.RequestStatistics;
                if (this.shared.MessagingOptions.ResendOnTimeout && this.shared.ShouldResend(msg))
                {
                    if (this.shared.Logger.IsEnabled(LogLevel.Debug)) this.shared.Logger.Debug(resendLogMessageFormat, msg.ResendCount, msg);
                    return;
                }

                if (requestStatistics.CollectApplicationRequestsStats)
                {
                    this.stopwatch.Stop();
                }

                this.shared.Unregister(this.Message);

                if (requestStatistics.CollectApplicationRequestsStats)
                {
                    requestStatistics.OnAppRequestsEnd(this.stopwatch.Elapsed);
                    if (isOnTimeout)
                    {
                        requestStatistics.OnAppRequestsTimedOut();
                    }
                }

                this.shared.ResponseCallback(error, this.context);
            }
        }
    }
}
