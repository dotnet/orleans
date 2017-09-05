using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{

    /// <summary>
    /// Config for event bulking feature
    /// </summary>
    public class EventBulkingConfig
    {
        /// <summary>
        /// Count limit for bulk event output.
        /// If the same event code is written more than <c>BulkEventLimit</c> times in the <c>BulkEventInterval</c> time period, 
        /// then only the first <c>BulkEventLimit</c> individual events will be written, plus a count of how bulk event suppressed.
        /// </summary>
        public int BulkEventLimit { get; set; } = DefaultBulkEventLimit;

        /// <summary>
        /// Default bulk event limit. 
        /// </summary>
        public const int DefaultBulkEventLimit = 5;

        /// <summary>
        /// Time limit for bulk event output.
        /// If the same event code is written more than <c>BulkEventLimit</c> times in the <c>BulkEventInterval</c> time period, 
        /// then only the first <c>BulkEventLimit</c> individual events will be written, plus a count of how bulk event suppressed.
        /// </summary>
        public TimeSpan BulkEventInterval { get; set; } = DefaultBulkEventInterval;

        /// <summary>
        /// Default bulk event interval
        /// </summary>
        public static readonly TimeSpan DefaultBulkEventInterval = TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// OrleansLoggingDecorator class. User can plug in their own ILogger implementation into this decorator class to add event bulking feature on top of their logger. 
    /// Event bulking feature will just log eventId count if the same eventId has appear more than BulkMessageLimit in a certain BulkMessageInterval.
    /// </summary>
    public class EventBulkingDecoratorLogger : ILogger
    {
        private static readonly int[] excludedBulkLogCodes = {
            0,
            100000 //internal runtime error code
        };
        private const int BulkEventSummaryOffset = 500000;

        private readonly EventBulkingConfig eventBulkingConfig;
        private readonly ConcurrentDictionary<int, int> recentLogMessageCounts = new ConcurrentDictionary<int, int>();
        private long lastBulkLogMessageFlushTicks = DateTime.MinValue.Ticks;
        private readonly ILogger decoratedLogger;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="config"></param>
        /// <param name="decoratedLogger"></param>
        public EventBulkingDecoratorLogger(EventBulkingConfig config, ILogger decoratedLogger)
        {
            this.eventBulkingConfig = config == null ? new EventBulkingConfig() : config;
            this.decoratedLogger = decoratedLogger;
        }

        /// <inheritdoc/>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            var errorCode = eventId.Id;
            if (CheckBulkMessageLimits(errorCode, logLevel))
                this.decoratedLogger.Log<TState>(logLevel, eventId, state, exception, formatter);
        }

        /// <inheritdoc/>
        public virtual bool IsEnabled(LogLevel logLevel)
        {
            return this.decoratedLogger.IsEnabled(logLevel);
        }

        /// <inheritdoc/>
        public IDisposable BeginScope<TState>(TState state)
        {
            return this.decoratedLogger.BeginScope<TState>(state);
        }

        private bool CheckBulkMessageLimits(int logCode, LogLevel sev)
        {
            var now = DateTime.UtcNow;
            int count;
            bool isExcluded = excludedBulkLogCodes.Contains(logCode)
                              || (sev == LogLevel.Debug || sev == LogLevel.Trace);
            
            // Increment recent message counts, if appropriate
            if (isExcluded)
            {
                count = 1;
                // and don't track counts
            }
            else
            {
                recentLogMessageCounts.AddOrUpdate(logCode, 1, (key, value) => ++value);
                recentLogMessageCounts.TryGetValue(logCode, out count);
            }

            var sinceIntervalTicks = now.Ticks - lastBulkLogMessageFlushTicks;
            //if it is time to flush bulked messages
            if (sinceIntervalTicks >= this.eventBulkingConfig.BulkEventInterval.Ticks)
            {
                // Take local copy of pending bulk message counts, now that this bulk message compaction period has finished
                var bulkMessageCounts = recentLogMessageCounts.Where(keyPair => keyPair.Value >= this.eventBulkingConfig.BulkEventLimit).ToList();
                recentLogMessageCounts.Clear();
                //set lastBulkLogMessageFlushTicks to now
                Interlocked.Exchange(ref this.lastBulkLogMessageFlushTicks, now.Ticks);
          
                // Output any pending bulk compaction messages
                if (bulkMessageCounts != null)
                {
                    // Output summary counts for any pending bulk message occurrances
                    foreach (var logCodeCountPair in bulkMessageCounts)
                    {
                        this.decoratedLogger.Log<string>(LogLevel.Information, new EventId(logCodeCountPair.Key + BulkEventSummaryOffset),
                                $"EventId {logCodeCountPair.Key} occurred {logCodeCountPair.Value - this.eventBulkingConfig.BulkEventLimit} additional time(s) in previous {new TimeSpan(sinceIntervalTicks)}", 
                                null, (msg, exc) => msg);
                    }
                }
            }

            // Should the current log message be output?
            return isExcluded || (count < this.eventBulkingConfig.BulkEventLimit);
        }
    }
}
