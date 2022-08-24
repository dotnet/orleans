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
            this.Unregister = unregister;
            this.Logger = logger;
            this.MessagingOptions = messagingOptions;
            this.ResponseTimeout = responseTimeout;
        }

        public TimeSpan ResponseTimeout
        {
            get => this.responseTimeout;
            set
            {
                this.responseTimeout = value;
                this.ResponseTimeoutStopwatchTicks = (long)(value.TotalSeconds * Stopwatch.Frequency);
            }
        }
    }
}