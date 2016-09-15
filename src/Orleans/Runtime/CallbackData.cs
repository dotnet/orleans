using System;
using System.Collections.Concurrent;
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

    internal class CallbackData : ITimebound, IDisposable
    {
        private readonly Action<Message, TaskCompletionSource<object>> callback;
        private readonly Func<Message, bool> resendFunc;
        private readonly Action unregister;
        private readonly TaskCompletionSource<object> context;

        private TimeSpan? resendPeriod;
        private bool alreadyFired;
        private TimeSpan timeout;
        private DateTime dueTime;
        private ITimeInterval timeSinceIssued;
        private IMessagingConfiguration config;

        [ThreadStatic]
        private static CallbackTimeouter callbackTimeouter;

        private static readonly Logger logger = LogManager.GetLogger("CallbackData");

        public Message Message { get; set; } // might hold metadata used by response pipeline

        public CallbackData(
            Action<Message, TaskCompletionSource<object>> callback,
            Func<Message, bool> resendFunc,
            TaskCompletionSource<object> ctx,
            Message msg,
            Action unregisterDelegate,
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

            if (callbackTimeouter == null) callbackTimeouter = new CallbackTimeouter(config.ResendOnTimeout);
            TimeSpan firstPeriod = timeout;
            if (config.ResendOnTimeout && config.MaxResendCount > 0)
            {
                resendPeriod = firstPeriod = timeout.Divide(config.MaxResendCount + 1);
                dueTime = DateTime.UtcNow.Add(firstPeriod);
                callbackTimeouter.RegisterResendable(this);
                return;
            }

            dueTime = DateTime.UtcNow.Add(firstPeriod);
            callbackTimeouter.Register(this);
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
                if (StatisticsCollector.CollectApplicationRequestsStats)
                {
                    timeSinceIssued.Stop();
                }
                unregister?.Invoke();
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
            var timeouter = callbackTimeouter;
            if (timeouter != null)
            {
                try
                {
                    callbackTimeouter.Dispose();
                    timeouter.Dispose();
                }
                catch { }
            }

            GC.SuppressFinalize(this);
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
                if (StatisticsCollector.CollectApplicationRequestsStats)
                {
                    timeSinceIssued.Stop();
                }

                unregister?.Invoke();
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

        private class CallbackTimeouter : IDisposable
        {
            private readonly QueueChecker queueChecker;
            private readonly QueueChecker resendableQueueChecker;

            public CallbackTimeouter(bool resendOnTimeout)
            {
                TimeSpan timerPeriod = TimeSpan.FromMilliseconds(70);
                queueChecker = new QueueChecker(timerPeriod, (queue, callback) => { });

                if (!resendOnTimeout)
                {
                    return;
                }

                // different queues is needed because due time of resendable callback differs from due time of ordinary one
                resendableQueueChecker = new QueueChecker(timerPeriod, (queue, callback) =>
                {
                    if (callback.resendPeriod.HasValue)
                    {
                        callback.dueTime = DateTime.UtcNow + callback.resendPeriod.Value;
                    }

                    queue.Enqueue(callback);
                });
            }


            public void Register(CallbackData data)
            {
                queueChecker.Register(data);
            }

            public void RegisterResendable(CallbackData data)
            {
                if (resendableQueueChecker == null)
                {
                    throw new InvalidOperationException("Can't enqueue resendable callback when initialized with resendOnCallback = false");
                }

                resendableQueueChecker.Register(data);
            }

            public void Dispose()
            {
                queueChecker.Dispose();
                resendableQueueChecker?.Dispose();
            }

            private class QueueChecker : IDisposable
            {
                private readonly Queue<CallbackData> callbacks = new Queue<CallbackData>();
                private readonly FastLock queueLock = new FastLock();
                private readonly SafeTimer queueChecker;
                private readonly Action<Queue<CallbackData>, CallbackData> _onTimeout;

                public QueueChecker(TimeSpan timerPeriod, Action<Queue<CallbackData>, CallbackData> onTimeout)
                {
                    _onTimeout = @onTimeout;
                    queueChecker = new SafeTimer(state =>
                    {
                        CheckQueue();
                    }, null, timerPeriod, timerPeriod);
                }

                public void Register(CallbackData data)
                {
                    queueLock.Take();
                    callbacks.Enqueue(data);
                    queueLock.Release();
                }

                // Crawls through the callbacks and timeouts expired ones
                private void CheckQueue()
                {
                    var now = DateTime.UtcNow;
                    while (true)
                    {
                        queueLock.Take();
                        int maxItemsPerLock = 7;
                        for (int i = 0; i < maxItemsPerLock; i++)
                        {
                            if (callbacks.Count == 0)
                            {
                                queueLock.Release();
                                return;
                            }

                            var callback = callbacks.Peek();
                            if (callback.alreadyFired)
                            {
                                callbacks.Dequeue();
                                continue;
                            }

                            if (callback.dueTime < now)
                            {
                                callbacks.Dequeue();
                                callback.OnTimeout();
                                _onTimeout(callbacks, callback);
                            }
                            else
                            {
                                queueLock.Release();
                                return;
                            }
                        }

                        queueLock.Release();
                    }
                }

                public void Dispose()
                {
                    queueChecker.Dispose();
                }
            }
        }
    }
}
