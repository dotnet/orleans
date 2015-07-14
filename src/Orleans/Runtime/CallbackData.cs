/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
        private readonly Action unregister;
        private readonly TaskCompletionSource<object> context;

        private bool alreadyFired;
        private TimeSpan timeout;
        private readonly Action<CallbackData> onTimeout;
        
        private SafeTimer timer;
        private ITimeInterval timeSinceIssued;
        private static readonly TraceLogger logger = TraceLogger.GetLogger("CallbackData");

        internal static IMessagingConfiguration Config;

        public Message Message { get; set; } // might hold metadata used by response pipeline

        public CallbackData(Action<Message, TaskCompletionSource<object>> callback, Func<Message, bool> resendFunc, TaskCompletionSource<object> ctx, Message msg, Action unregisterDelegate, Action<CallbackData> onTimeout = null)
        {
            // We are never called without a callback func, but best to double check.
            if (callback == null) throw new ArgumentNullException("callback");
            // We are never called without a resend func, but best to double check.
            if (resendFunc == null) throw new ArgumentNullException("resendFunc");

            this.callback = callback;
            this.resendFunc = resendFunc;
            this.context = ctx;
            this.Message = msg;
            this.unregister = unregisterDelegate;
            this.alreadyFired = false;
            this.onTimeout = onTimeout;
        }

        /// <summary>
        /// Start this callback timer
        /// </summary>
        /// <param name="time">Timeout time</param>
        public void StartTimer(TimeSpan time)
        {
            if (time < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException("time", "The timeout parameter is negative.");
            timeout = time;
            if (StatisticsCollector.CollectApplicationRequestsStats)
            {
                timeSinceIssued = TimeIntervalFactory.CreateTimeInterval(true);
                timeSinceIssued.Start();
            }

            TimeSpan firstPeriod = timeout;
            TimeSpan repeatPeriod = Constants.INFINITE_TIMESPAN; // Single timeout period --> No repeat
            if (Config.ResendOnTimeout && Config.MaxResendCount > 0)
            {
                firstPeriod = repeatPeriod = timeout.Divide(Config.MaxResendCount + 1);
            }
            // Start time running
            DisposeTimer();
            timer = new SafeTimer(TimeoutCallback, null, firstPeriod, repeatPeriod);

        }

        private void TimeoutCallback(object obj)
        {
            if (onTimeout != null)
            {
                onTimeout(this);
            }
            else
            {
                OnTimeout();
            }
        }

        public void OnTimeout()
        {
            if (alreadyFired)
                return;
            var msg = this.Message; // Local working copy
            lock (this)
            {
                if (alreadyFired)
                    return;

                if (Config.ResendOnTimeout && resendFunc(msg))
                {
                    if(logger.IsVerbose) logger.Verbose("OnTimeout - Resend {0} for {1}", msg.ResendCount, msg);
                    return;
                }

                alreadyFired = true;
                DisposeTimer();
                if (StatisticsCollector.CollectApplicationRequestsStats)
                {
                    timeSinceIssued.Stop();
                }

                if (unregister != null)
                {
                    unregister();
                }
            }

            string messageHistory = msg.GetTargetHistory();
            string errorMsg = String.Format("Response did not arrive on time in {0} for message: {1}. Target History is: {2}",
                                timeout, msg, messageHistory);
            logger.Warn(ErrorCode.Runtime_Error_100157, "{0}. About to break its promise.", errorMsg);

            var error = new Message(Message.Categories.Application, Message.Directions.Response)
            {
                Result = Message.ResponseTypes.Error,
                BodyObject = Response.ExceptionResponse(new TimeoutException(errorMsg))
            };
            if (StatisticsCollector.CollectApplicationRequestsStats)
            {
                ApplicationRequestsStatisticsGroup.OnAppRequestsEnd(timeSinceIssued.Elapsed);
                ApplicationRequestsStatisticsGroup.OnAppRequestsTimedOut();
            }

            callback(error, context);
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
                if (unregister != null)
                {
                    unregister();
                }     
            }
            if (Message.WriteMessagingTraces) response.AddTimestamp(Message.LifecycleTag.InvokeIncoming);
            if (logger.IsVerbose2) logger.Verbose2("Message {0} timestamps: {1}", response, response.GetTimestampString());
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
                if (timer != null)
                {
                    var tmp = timer;
                    timer = null;
                    tmp.Dispose();
                }
            }
            catch (Exception) { } // Ignore any problems with Dispose
        }

        public TimeSpan RequestedTimeout()
        {
            return timeout;
        }
    }
}
