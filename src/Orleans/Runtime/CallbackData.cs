using System;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    /// <summary>
    /// This interface is for use with the Orleans timers.
    /// </summary>
    internal interface ITimebound
    {
        /// <summary>
        /// This method is called by the timer when the time out is reached.
        /// </summary>
        void OnTimeout();
        TimeSpan RequestedTimeout();
    }

    internal class CallbackData : ITimebound, IDisposable
    {
        private readonly Action<Message, TaskCompletionSource<object>> callback;
        private readonly Func<Message, bool> resendFunc;
        private readonly Action<Message> unregister;
        private readonly TaskCompletionSource<object> context;
        private readonly IMessagingConfiguration config;

        private bool alreadyFired;
        private TimeSpan timeout;
        private SafeTimer timer;
        private ITimeInterval timeSinceIssued;
        private static readonly Logger logger = LogManager.GetLogger("CallbackData");

        public Message Message { get; set; } // might hold metadata used by response pipeline

        public CallbackData(
            Action<Message, TaskCompletionSource<object>> callback, 
            Func<Message, bool> resendFunc, 
            TaskCompletionSource<object> ctx, 
            Message msg, 
            Action<Message> unregisterDelegate,
            IMessagingConfiguration config)
        {
            // We are never called without a callback func, but best to double check.
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            // We are never called without a resend func, but best to double check.
            if (resendFunc == null) throw new ArgumentNullException(nameof(resendFunc));

            this.callback = callback;
            this.resendFunc = resendFunc;
            context = ctx;
            Message = msg;
            unregister = unregisterDelegate;
            alreadyFired = false;
            this.config = config;
        }

        /// <summary>
        /// Start this callback timer
        /// </summary>
        /// <param name="time">Timeout time</param>
        public void StartTimer(TimeSpan time)
        {
            if (time < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(time), "The timeout parameter is negative.");
            timeout = time;
            if (StatisticsCollector.CollectApplicationRequestsStats)
            {
                timeSinceIssued = TimeIntervalFactory.CreateTimeInterval(true);
                timeSinceIssued.Start();
            }

            TimeSpan firstPeriod = timeout;
            TimeSpan repeatPeriod = Constants.INFINITE_TIMESPAN; // Single timeout period --> No repeat
            if (config.ResendOnTimeout && config.MaxResendCount > 0)
            {
                firstPeriod = repeatPeriod = timeout.Divide(config.MaxResendCount + 1);
            }
            // Start time running
            DisposeTimer();
            timer = new SafeTimer(TimeoutCallback, null, firstPeriod, repeatPeriod);

        }

        private void TimeoutCallback(object obj)
        {
            OnTimeout();
        }

        public void OnTimeout()
        {
            if (alreadyFired)
                return;
            var msg = Message; // Local working copy

            string messageHistory = msg.GetTargetHistory();
            string errorMsg = $"Response did not arrive on time in {timeout} for message: {msg}. Target History is: {messageHistory}.";
            logger.Warn(ErrorCode.Runtime_Error_100157, "{0} About to break its promise.", errorMsg);

            var error = msg.CreatePromptExceptionResponse(new TimeoutException(errorMsg));
            OnFail(msg, error, "OnTimeout - Resend {0} for {1}", true);
        }

        public void OnTargetSiloFail()
        {
            if (alreadyFired)
                return;

            var msg = Message;
            var messageHistory = msg.GetTargetHistory();
            string errorMsg = 
                $"The target silo became unavailable for message: {msg}. Target History is: {messageHistory}. See {Constants.TroubleshootingHelpLink} for troubleshooting help.";
            logger.Warn(ErrorCode.Runtime_Error_100157, "{0} About to break its promise.", errorMsg);

            var error = msg.CreatePromptExceptionResponse(new SiloUnavailableException(errorMsg));
            OnFail(msg, error, "On silo fail - Resend {0} for {1}");
        }

        public void DoCallback(Message response)
        {
            if (alreadyFired)
                return;
            lock (this)
            {
                if (alreadyFired)
                    return;

                if (response.Result == Message.ResponseTypes.Rejection && response.RejectionType == Message.RejectionTypes.Transient)
                {
                    if (resendFunc(Message))
                    {
                        return;
                    }
                }

                alreadyFired = true;
                DisposeTimer();
                if (StatisticsCollector.CollectApplicationRequestsStats)
                {
                    timeSinceIssued.Stop();
                }
                unregister?.Invoke(Message);
            }
            if (StatisticsCollector.CollectApplicationRequestsStats)
            {
                ApplicationRequestsStatisticsGroup.OnAppRequestsEnd(timeSinceIssued.Elapsed);
            }
            // do callback outside the CallbackData lock. Just not a good practice to hold a lock for this unrelated operation.
            callback(response, context);
        }

        public void Dispose()
        {
            DisposeTimer();
            GC.SuppressFinalize(this);
        }

        private void DisposeTimer()
        {
            try
            {
                var tmp = timer;
                if (tmp != null)
                {
                    timer = null;
                    tmp.Dispose();
                }
            }
            catch (Exception) { } // Ignore any problems with Dispose
        }

        private void OnFail(Message msg, Message error, string resendLogMessageFormat, bool isOnTimeout = false)
        {
            lock (this)
            {
                if (alreadyFired)
                    return;

                if (config.ResendOnTimeout && resendFunc(msg))
                {
                    if (logger.IsVerbose) logger.Verbose(resendLogMessageFormat, msg.ResendCount, msg);
                    return;
                }

                alreadyFired = true;
                DisposeTimer();
                if (StatisticsCollector.CollectApplicationRequestsStats)
                {
                    timeSinceIssued.Stop();
                }

                unregister?.Invoke(Message);
            }
            
            if (StatisticsCollector.CollectApplicationRequestsStats)
            {
                ApplicationRequestsStatisticsGroup.OnAppRequestsEnd(timeSinceIssued.Elapsed);
                if (isOnTimeout)
                {
                    ApplicationRequestsStatisticsGroup.OnAppRequestsTimedOut();
                }
            }

            callback(error, context);
        }

        public TimeSpan RequestedTimeout()
        {
            return timeout;
        }
    }
}
