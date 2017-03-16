using System;
using System.Collections.Generic;
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
   

    internal interface CallbackData : ITimebound, IDisposable
    {
        void DoCallback(Message response);
        Message Message { get; set; }
        DateTime DueTime { get; set; }
        void OnTargetSiloFail();
        bool alreadyFired { get; }
        void OnTimeout();
    }

    internal static class CallbackDataTimerWheelInstance
    {
        private static readonly TimeSpan _callbacksWheelCheckPeriod =
          System.Diagnostics.Debugger.IsAttached ? TimeSpan.FromSeconds(60) : TimeSpan.FromMilliseconds(3077);
        public static TimerWheel<CallbackData> CallbacksWheel = new TimerWheel<CallbackData>(_callbacksWheelCheckPeriod);
    }

    internal class CallbackData<T> : CallbackData
    {
        private readonly Action<Message, TaskCompletionSource<T>> callback;
        private readonly Func<Message, bool> resendFunc;
        private readonly Action<Message> unregister;
        private readonly TaskCompletionSource<T> context;
        private readonly IMessagingConfiguration config;
          private TimeSpan timeout;
        private TimeSpan? resendPeriod;
        private SafeTimer timer;
        private CallbackEntityHolder _callbackHolder;
        private ITimeInterval timeSinceIssued;
        private static readonly Logger logger = LogManager.GetLogger("CallbackData");

        public bool alreadyFired { get; private set; }
        public Message Message { get; set; }
        public DateTime DueTime { get; set; }
// might hold metadata used by response pipeline

        public CallbackData(
            Action<Message, TaskCompletionSource<T>> callback, 
            Func<Message, bool> resendFunc, 
            TaskCompletionSource<T> ctx, 
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
        public void RegisterTimeout(TimeSpan time)
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
            if (config.ResendOnTimeout && config.MaxResendCount > 0)
            {
                resendPeriod = firstPeriod = timeout.Divide(config.MaxResendCount + 1);
                DueTime = DateTime.UtcNow.Add(firstPeriod);
                _callbackHolder = new ResendableMessageCallbackEntityHolder(this, DueTime);
                CallbackDataTimerWheelInstance.CallbacksWheel.CheckQueueAndRegister(_callbackHolder);
                return;
            }

            DueTime = DateTime.UtcNow.Add(firstPeriod);
            _callbackHolder = new CallbackEntityHolder(this, DueTime);
            CallbackDataTimerWheelInstance.CallbacksWheel.CheckQueueAndRegister(_callbackHolder);
        }
        

        public void OnTimeout()
        {
            if (alreadyFired)
                return;
            var msg = Message; // Local working copy

            string messageHistory = msg.GetTargetHistory();
            string errorMsg = $"Response did not arrive on time in {timeout} for message: {msg}. Target History is: {messageHistory}.";
            logger.Warn(ErrorCode.Runtime_Error_100157, "{0} About to break its promise.", errorMsg);

            var error = Message.CreatePromptExceptionResponse(msg, new TimeoutException(errorMsg));
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

            var error = Message.CreatePromptExceptionResponse(msg, new SiloUnavailableException(errorMsg));
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

        private void DisposeTimer() // todo: rename
        {
            try
            {
             _callbackHolder?.RemoveCallbackReference();
              _callbackHolder = null;
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

        internal class CallbackEntityHolder : TimeboundEntityHolder<CallbackData>
        {
            public CallbackEntityHolder(CallbackData data, DateTime dueTime) : base(data, dueTime)
            {
            }

            public override bool AvailableForDequeue
            {
                get { return Entity.alreadyFired; }
            }
            
            public override void OnTimeout(Queue<TimeboundEntityHolder<CallbackData>> queue)
            {
                Entity.OnTimeout();
            }

            public void RemoveCallbackReference()
            {
                Entity = DummyCallback.Instance;
            }

            private class DummyCallback : CallbackData
            {
                public static DummyCallback Instance = new DummyCallback();
                public bool alreadyFired
                {
                    get
                    {
                        return true;
                    }
                }

                public DateTime DueTime
                {
                    get
                    {
                        throw new NotImplementedException();
                    }

                    set
                    {
                        throw new NotImplementedException();
                    }
                }

                public Message Message
                {
                    get
                    {
                        throw new NotImplementedException();
                    }

                    set
                    {
                        throw new NotImplementedException();
                    }
                }

                public void Dispose()
                {
                }

                public void DoCallback(Message response)
                {
                }

                public void OnTargetSiloFail()
                {
                    throw new NotImplementedException();
                }

                public void OnTimeout()
                {
                }

                public TimeSpan RequestedTimeout()
                {
                    throw new NotImplementedException();
                }
            }
        }
        internal class ResendableMessageCallbackEntityHolder : CallbackEntityHolder
        {
            public ResendableMessageCallbackEntityHolder(CallbackData data, DateTime dueTime) : base(data, dueTime)
            {
            }
            public override void OnTimeout(Queue<TimeboundEntityHolder<CallbackData>> queue)
            {
                // todo
                // resendPeriod = firstPeriod = timeout.Divide(config.MaxResendCount + 1); 
                base.OnTimeout(queue);
            }
        }
    }
}
