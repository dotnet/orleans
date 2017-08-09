using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions.Internal;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Orleans.Extensions.Logging
{
    /// <summary>
    /// OreansLogger supports legacy orleans logging features, including <see cref="ILogConsumer"/>, <see cref="ICloseableLogConsumer">,
    /// <see cref="IFlushableLogConsumer">, <see cref="Severity">, message bulking. 
    /// </summary>
    public class OrleansLogger : ILogger
    {
        private static readonly int[] excludedBulkLogCodes = {
            0,
            (int)ErrorCode.Runtime
        };
        private const int BulkMessageSummaryOffset = 500000;
        private const string LogCodeString = "OrleansLogCode: ";
        private readonly TimeSpan flushInterval = Debugger.IsAttached ? TimeSpan.FromMilliseconds(10) : TimeSpan.FromSeconds(1);
        private DateTime lastFlush = DateTime.UtcNow;

        private MessageBulkingConfig messageBulkingConfig;
        private Dictionary<int, int> recentLogMessageCounts = new Dictionary<int, int>();
        private DateTime lastBulkLogMessageFlush = DateTime.MinValue;
        private IList<ILogConsumer> logConsumers;
        private Severity maxSeverityLevel;
        private string name;
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="categoryName"></param>
        /// <param name="logConsumers"></param>
        /// <param name="maxSeverityLevel"></param>
        /// <param name="bulkingConfig"></param>
        public OrleansLogger(string categoryName, IList<ILogConsumer> logConsumers, Severity maxSeverityLevel, MessageBulkingConfig bulkingConfig)
        {
            this.logConsumers = logConsumers;
            this.maxSeverityLevel = maxSeverityLevel;
            this.name = categoryName;
            this.messageBulkingConfig = bulkingConfig == null ? new MessageBulkingConfig() : bulkingConfig;
        }

        /// <inheritdoc/>
        public IDisposable BeginScope<TState>(TState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }
            //TODO: support logging scope
            return NullScope.Instance;
        }

        /// <inheritdoc/>
        public bool IsEnabled(LogLevel logLevel)
        {
            var severity = LogLevelToSeverity(logLevel);
            return severity <= maxSeverityLevel;
        }

        /// <summary>
        /// Create EventId in a format which supports message bulking
        /// </summary>
        /// <param name="eventId"></param>
        /// <param name="errorCode"></param>
        /// <returns></returns>
        public static EventId CreateEventId(int eventId, int errorCode)
        {
            return new EventId(eventId, $"{LogCodeString} {errorCode}");
        }

        /// <summary>
        /// Get error code from EventId
        /// </summary>
        /// <param name="eventId"></param>
        /// <returns></returns>
        public static int? GetOrleansErrorCode(EventId eventId)
        {
            if (eventId.Name.Contains(LogCodeString))
            {
                var errorCodeString = eventId.Name.Substring(LogCodeString.Length);
                int errorCode = 0;
                if (int.TryParse(errorCodeString, out errorCode))
                {
                    return errorCode;
                } 
            }
            return null;
        }
        /// <summary>
        /// Log a message. Current logger supports legacy message bulking feature, only when <param name="eventId"> contains errorCode information in a certain format. 
        /// For example, in order to use message bulking feature, one need to use eventId = OrleansLogger.CreateEventId(eventId, errorCode) to create a EventId which fulfils the certain format.
        /// Or one can use extension method <see cref="ILogger.Log(this ILogger logger, int errorCode, Severity sev, string format, object[] args, Exception exception)"/> to achieve this.
        /// </summary>
        /// <typeparam name="TState"></typeparam>
        /// <param name="logLevel"></param>
        /// <param name="eventId"></param>
        /// <param name="state"></param>
        /// <param name="exception"></param>
        /// <param name="formatter"></param>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            var errorCode = GetOrleansErrorCode(eventId);
            var severity = LogLevelToSeverity(logLevel);
            if (errorCode.HasValue)
            {
                //orleans legacy logging style
                if (CheckBulkMessageLimits(errorCode.Value, severity))
                    WriteLogMessageToLogConsumers(errorCode.Value, severity, formatter(state, exception), exception);
            }
            else
            {
                //normal logging style
                WriteLogMessageToLogConsumers(errorCode.Value, severity, formatter(state, exception), exception);
            }
        }

        /// <summary>
        /// Map LogLevel to Severity
        /// </summary>
        /// <param name="logLevel"></param>
        /// <returns></returns>
        public static Severity LogLevelToSeverity(LogLevel logLevel)
        {
            switch (logLevel)
            {
                case LogLevel.None: return Severity.Off;
                case LogLevel.Critical: return Severity.Critical;
                case LogLevel.Error: return Severity.Error;
                case LogLevel.Warning: return Severity.Warning;
                case LogLevel.Information: return Severity.Info;
                case LogLevel.Debug: return Severity.Verbose;
                default: return Severity.Verbose3;
            }
        }

        /// <summary>
        /// Map Severity to LogLevel
        /// </summary>
        /// <param name="severity"></param>
        /// <returns></returns>
        public static LogLevel SeverityToLogLevel(Severity severity)
        {
            switch (severity)
            {
                case Severity.Off: return LogLevel.None;
                case Severity.Critical: return LogLevel.Critical;
                case Severity.Error: return LogLevel.Error;
                case Severity.Warning: return LogLevel.Warning;
                case Severity.Info: return LogLevel.Information;
                case Severity.Verbose: return LogLevel.Debug;
                default: return LogLevel.Trace;
            }
        }

        private void WriteLogMessageToLogConsumers(int errorCode, Severity sev, string message, Exception exception)
        {
            foreach (ILogConsumer consumer in this.logConsumers)
            {
                try
                {
                    consumer.Log(sev, this.name, message, exception, errorCode);
                }
                catch (Exception exc)
                {
                    Console.WriteLine("Exception while passing a log message to log consumer. LogConsumer type:{0}, logger name:{1}, severity:{2}, message:{3}, error code:{4}, message exception:{5}, log consumer exception:{6}",
                        consumer.GetType().FullName, this.name, sev, message, errorCode, exception, exc);
                }
            }

            //flush flushable consumers
            if ((DateTime.UtcNow - lastFlush) > flushInterval)
            {
                lastFlush = DateTime.UtcNow;
                foreach (IFlushableLogConsumer consumer in this.logConsumers.OfType<IFlushableLogConsumer>())
                {
                    consumer.Flush();
                }
            }
        }

        private bool CheckBulkMessageLimits(int logCode, Severity sev)
        {
            var now = DateTime.UtcNow;
            int count;
            TimeSpan sinceInterval;
            Dictionary<int, int> copyMessageCounts = null;

            bool isExcluded = excludedBulkLogCodes.Contains(logCode)
                              || (sev == Severity.Verbose || sev == Severity.Verbose2 || sev == Severity.Verbose3);

            lock (this)
            {
                sinceInterval = now - lastBulkLogMessageFlush;
                if (sinceInterval >= this.messageBulkingConfig.BulkMessageInterval)
                {
                    // Take local copy of buffered log message counts, now that this bulk message compaction period has finished
                    copyMessageCounts = recentLogMessageCounts;
                    recentLogMessageCounts = new Dictionary<int, int>();
                    lastBulkLogMessageFlush = now;
                }

                // Increment recent message counts, if appropriate
                if (isExcluded)
                {
                    count = 1;
                    // and don't track counts
                }
                else if (recentLogMessageCounts.ContainsKey(logCode))
                {
                    count = ++recentLogMessageCounts[logCode];
                }
                else
                {
                    recentLogMessageCounts.Add(logCode, 1);
                    count = 1;
                }
            }

            // Output any pending bulk compaction messages
            if (copyMessageCounts != null && copyMessageCounts.Count > 0)
            {
                object[] args = new object[4];
                args[3] = sinceInterval;

                // Output summary counts for any pending bulk message occurrances
                foreach (int ec in copyMessageCounts.Keys)
                {
                    int num = copyMessageCounts[ec] - this.messageBulkingConfig.BulkMessageLimit;

                    // Only output log codes which exceeded limit threshold
                    if (num > 0)
                    {
                        WriteLogMessageToLogConsumers(ec + BulkMessageSummaryOffset, Severity.Info, $"Log code {args[0]} occurred {args[1]} additional time(s)", null);
                    }
                }
            }

            // Should the current log message be output?
            return isExcluded || (count <= this.messageBulkingConfig.BulkMessageLimit);
        }
    }
}
