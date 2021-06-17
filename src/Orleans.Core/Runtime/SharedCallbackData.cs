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
            ApplicationRequestsStatisticsGroup requestStatistics,
            TimeSpan responseTimeout)
        {
            RequestStatistics = requestStatistics;
            this.Unregister = unregister;
            this.Logger = logger;
            this.MessagingOptions = messagingOptions;
            this.ResponseTimeout = responseTimeout;
        }

        public ApplicationRequestsStatisticsGroup RequestStatistics { get; }

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