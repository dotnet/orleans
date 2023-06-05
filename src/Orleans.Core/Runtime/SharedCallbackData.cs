using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace Orleans.Runtime
{
    internal class SharedCallbackData
    {
        public readonly Action<Message> Unregister;
        public readonly ILogger Logger;
        public readonly MessagingOptions MessagingOptions;
        private TimeSpan responseTimeout;
        public long ResponseTimeoutStopwatchTicks;

        public SharedCallbackData(
            Action<Message> unregister,
            ILogger logger,
            MessagingOptions messagingOptions,
            TimeSpan responseTimeout)
        {
            Unregister = unregister;
            Logger = logger;
            MessagingOptions = messagingOptions;
            ResponseTimeout = responseTimeout;
        }

        public TimeSpan ResponseTimeout
        {
            get => responseTimeout;
            set
            {
                responseTimeout = value;
                ResponseTimeoutStopwatchTicks = (long)(value.TotalSeconds * Stopwatch.Frequency);
            }
        }
    }
}