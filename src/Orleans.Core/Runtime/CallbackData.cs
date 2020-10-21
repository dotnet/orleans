using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Transactions;

namespace Orleans.Runtime
{
    internal class CallbackData
    {
        private readonly SharedCallbackData shared;
        private readonly TaskCompletionSource<object> context;
        private int completed;
        private StatusResponse lastKnownStatus;
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
            if (this.IsCompleted)
                return;

            OrleansCallBackDataEvent.Log.OnTimeout(this.Message);

            var msg = this.Message; // Local working copy

            string messageHistory = msg.GetTargetHistory();
            var statusMessage = lastKnownStatus is StatusResponse status ? $"Last known status is {status}. " : string.Empty;
            string errorMsg = $"Response did not arrive on time in {timeout} for message: {msg}. {statusMessage}Target History is: {messageHistory}.";
            this.shared.Logger.Warn(ErrorCode.Runtime_Error_100157, "{0} About to break its promise.", errorMsg);

            var error = Message.CreatePromptExceptionResponse(msg, new TimeoutException(errorMsg));
            OnFail(msg, error, isOnTimeout: true);
        }

        public void OnTargetSiloFail()
        {
            if (this.IsCompleted)
                return;
            OrleansCallBackDataEvent.Log.OnTargetSiloFail(this.Message);
            var msg = this.Message;
            var messageHistory = msg.GetTargetHistory();
            var statusMessage = lastKnownStatus is StatusResponse status ? $"Last known status is {status}. " : string.Empty;
            string errorMsg = 
                $"The target silo became unavailable for message: {msg}. {statusMessage}Target History is: {messageHistory}. See {Constants.TroubleshootingHelpLink} for troubleshooting help.";
            this.shared.Logger.Warn(ErrorCode.Runtime_Error_100157, "{0} About to break its promise.", errorMsg);

            var error = Message.CreatePromptExceptionResponse(msg, new SiloUnavailableException(errorMsg));
            OnFail(msg, error, isOnTimeout: false);
        }

        public void DoCallback(Message response)
        {
            if (this.IsCompleted)
                return;

            OrleansCallBackDataEvent.Log.DoCallback(this.Message);

            if (Interlocked.CompareExchange(ref this.completed, 1, 0) == 0)
            {
                var requestStatistics = this.shared.RequestStatistics;
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

        private void OnFail(Message msg, Message error, bool isOnTimeout = false)
        {
            if (Interlocked.CompareExchange(ref this.completed, 1, 0) == 0)
            {
                var requestStatistics = this.shared.RequestStatistics;
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
