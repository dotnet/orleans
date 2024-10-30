using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class SharedCallbackData
    {
        public readonly Action<Message> Unregister;
        public readonly ILogger Logger;
        private TimeSpan responseTimeout;
        public long ResponseTimeoutStopwatchTicks;
        public IGrainFactory GrainFactory;

        public SharedCallbackData(
            Action<Message> unregister,
            ILogger logger,
            IGrainFactory grainFactory,
            TimeSpan responseTimeout)
        {
            this.Unregister = unregister;
            this.Logger = logger;
            this.GrainFactory = grainFactory;
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