using Orleans.Runtime;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{

    /// <summary>
    /// Config for message bulking feature
    /// </summary>
    public class MessageBulkingConfig
    {
        /// <summary>
        /// Count limit for bulk message output.
        /// If the same log code is written more than <c>BulkMessageLimit</c> times in the <c>BulkMessageInterval</c> time period, 
        /// then only the first <c>BulkMessageLimit</c> individual messages will be written, plus a count of how bulk messages suppressed.
        /// </summary>
        public int BulkMessageLimit { get; set; } = DefaultBulkMessageLimit;

        /// <summary>
        /// Default bulk message limit. 
        /// </summary>
        public const int DefaultBulkMessageLimit = 5;

        /// <summary>
        /// Time limit for bulk message output.
        /// If the same log code is written more than <c>BulkMessageLimit</c> times in the <c>BulkMessageInterval</c> time period, 
        /// then only the first <c>BulkMessageLimit</c> individual messages will be written, plus a count of how bulk messages suppressed.
        /// </summary>
        public TimeSpan BulkMessageInterval { get; set; } = DefaultBulkMessageInterval;

        /// <summary>
        /// Default bulk message interval
        /// </summary>
        public static readonly TimeSpan DefaultBulkMessageInterval = TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// OrleansLoggingDecorator class. User can plug in their own ILogger implementation into this decorator class to add message bulking feature on top of their logger. 
    /// Message bulking feature will just log eventId count if the same eventId has appear more than BulkMessageLimit in a certain BulkMessageInterval.
    /// </summary>
    public class OrleansLoggingDecorator : ILogger
    {
        private static readonly int[] excludedBulkLogCodes = {
            0,
            100000 //internal runtime error code
        };
        private const int BulkMessageSummaryOffset = 500000;

        private readonly MessageBulkingConfig messageBulkingConfig;
        private readonly ConcurrentDictionary<int, int> recentLogMessageCounts = new ConcurrentDictionary<int, int>();
        private long lastBulkLogMessageFlushTicks = DateTime.MinValue.Ticks;
        private readonly ILogger thirdPartyLogger;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="config"></param>
        /// <param name="thirdPartyLogger"></param>
        public OrleansLoggingDecorator(MessageBulkingConfig config, ILogger thirdPartyLogger)
        {
            this.messageBulkingConfig = config == null ? new MessageBulkingConfig() : config;
            this.thirdPartyLogger = thirdPartyLogger;
        }

        /// <inheritdoc/>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            var errorCode = eventId.Id;
            if (CheckBulkMessageLimits(errorCode, logLevel))
                this.thirdPartyLogger.Log<TState>(logLevel, eventId, state, exception, formatter);
        }

        /// <inheritdoc/>
        public virtual bool IsEnabled(LogLevel logLevel)
        {
            return this.thirdPartyLogger.IsEnabled(logLevel);
        }

        /// <inheritdoc/>
        public IDisposable BeginScope<TState>(TState state)
        {
            return this.thirdPartyLogger.BeginScope<TState>(state);
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
                recentLogMessageCounts.AddOrUpdate(logCode, 1, (key, value) => value++);
                recentLogMessageCounts.TryGetValue(logCode, out count);
            }

            var sinceIntervalTicks = now.Ticks - lastBulkLogMessageFlushTicks;
            //if it is time to flush bulked messages
            if (sinceIntervalTicks >= this.messageBulkingConfig.BulkMessageInterval.Ticks)
            {
                // Take local copy of pending bulk message counts, now that this bulk message compaction period has finished
                var bulkMessageCounts = recentLogMessageCounts.Where(keyPair => keyPair.Value > this.messageBulkingConfig.BulkMessageLimit);
                recentLogMessageCounts.Clear();
                //set lastBulkLogMessageFlushTicks to now
                Interlocked.Exchange(ref this.lastBulkLogMessageFlushTicks, now.Ticks);
          
                // Output any pending bulk compaction messages
                if (bulkMessageCounts != null)
                {
                    // Output summary counts for any pending bulk message occurrances
                    foreach (var logCodeCountPair in bulkMessageCounts)
                    {
                        this.thirdPartyLogger.Log<string>(LogLevel.Information, new EventId(logCodeCountPair.Key + BulkMessageSummaryOffset),
                                $"EventId {logCodeCountPair.Key} occurred {logCodeCountPair.Value - this.messageBulkingConfig.BulkMessageLimit} additional time(s) in previous {new TimeSpan(sinceIntervalTicks)}", 
                                null, (msg, exc) => msg);
                    }
                }
            }

            // Should the current log message be output?
            return isExcluded || (count <= this.messageBulkingConfig.BulkMessageLimit);
        }
    }
}
