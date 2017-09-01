using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Orleans.Runtime
{
    /// <summary>
    /// The LoggerImpl class is the internal <see cref="Logger"/> implementation.  It directs log messages to sinks in <see cref="LogManager"/>
    /// </summary>
    [Serializable]
    internal class LoggerImpl : Logger
    {
        internal int defaultCopiedCounter;
        internal Severity severity;
        internal bool useCustomSeverityLevel;

        internal readonly LoggerType loggerType;

        private Dictionary<int, int> recentLogMessageCounts = new Dictionary<int, int>();
        private DateTime lastBulkLogMessageFlush = DateTime.MinValue;

        private readonly TimeSpan flushInterval = Debugger.IsAttached ? TimeSpan.FromMilliseconds(10) : TimeSpan.FromSeconds(1);
        private DateTime lastFlush = DateTime.UtcNow;

        /// <summary>List of log codes that won't have bulk message compaction policy applied to them</summary>
        /// <summary>
        /// The current severity level for this Logger.
        /// Log entries will be written if their severity is (logically) equal to or greater than this level.
        /// If it is not explicitly set, then a default value will be calculated based on the logger's type and name.
        /// Note that changes to the global default settings will be propagated to existing loggers that are using the default severity.
        /// </summary>
        public override Severity SeverityLevel
        {
            get
            {
                if (useCustomSeverityLevel || (defaultCopiedCounter >= LogManager.defaultModificationCounter)) return severity;

                severity = LogManager.GetDefaultSeverityForLog(Name, loggerType);
                defaultCopiedCounter = LogManager.defaultModificationCounter;
                return severity;
            }
        }

        /// <summary>
        /// Name of logger instance
        /// </summary>
        public override string Name { get; }

        public override Logger GetLogger(string loggerName)
        {
            return LogManager.GetLogger(loggerName, loggerType);
        }

        /// <summary>
        /// Set a new severity level for this Logger.
        /// Log entries will be written if their severity is (logically) equal to or greater than this level.
        /// </summary>
        /// <param name="sev">New severity level to be used for filtering log messages.</param>
        public void SetSeverityLevel(Severity sev)
        {
            severity = sev;
            useCustomSeverityLevel = true;
        }

        /// <summary>
        /// Constructs a Logger with the given name and type.
        /// </summary>
        /// <param name="source">The name of the source of log entries for this Logger.
        /// Typically this is the full name of the class that is using this Logger.</param>
        /// <param name="logType">The category of Logger to create.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2214:DoNotCallOverridableMethodsInConstructors")]
        internal LoggerImpl(string source, LoggerType logType)
        {
            defaultCopiedCounter = -1;
            Name = source;
            loggerType = logType;
            useCustomSeverityLevel = CheckForSeverityOverride();
        }

        internal bool MatchesPrefix(string prefix)
        {
            return Name.StartsWith(prefix, StringComparison.Ordinal)
                || (loggerType + "." + Name).StartsWith(prefix, StringComparison.Ordinal);
        }

        internal bool CheckForSeverityOverride()
        {
            lock (LogManager.lockable)
            {
                if (LogManager.traceLevelOverrides.Count <= 0) return false;

                foreach (var o in LogManager.traceLevelOverrides)
                {
                    if (!MatchesPrefix(o.Item1)) continue;

                    severity = o.Item2;
                    useCustomSeverityLevel = true;
                    return true;
                }
            }
            return false;
        }


        // an internal method to be used only by the runtime to ensure certain long report messages are logged fully, without truncating and bulking.
        internal void LogWithoutBulkingAndTruncating(Severity severityLevel, ErrorCode errorCode, string format, params object[] args)
        {
            if (severityLevel > SeverityLevel)
            {
                return;
            }

            string message = FormatMessageText(format, args);
            // skip bulking
            // break into chunks of smaller sizes 
            if (message.Length > LogManager.MAX_LOG_MESSAGE_SIZE)
            {
                int startIndex = 0;
                int maxChunkSize = LogManager.MAX_LOG_MESSAGE_SIZE - 100; // 100 bytes to allow slack and prefix.
                int partNum = 1;
                while (startIndex < message.Length)
                {
                    int chunkSize = (startIndex + maxChunkSize) < message.Length ? maxChunkSize : (message.Length - startIndex);
                    var messageToLog = $"CHUNKED MESSAGE Part {partNum}: {message.Substring(startIndex, chunkSize)}";
                    WriteLogMessage((int)errorCode, severity, messageToLog, null, null);
                    startIndex += chunkSize;
                    partNum++;
                }
            }
            else
            {
                WriteLogMessage((int)errorCode, severityLevel, message, null, null);
            }
        }

        public override void Log(int errorCode, Severity sev, string format, object[] args, Exception exception)
        {
            if (sev > SeverityLevel)
            {
                return;
            }

            if (errorCode == 0 && loggerType == LoggerType.Runtime)
            {
                errorCode = (int)ErrorCode.Runtime;
            }

            if (CheckBulkMessageLimits(errorCode, sev))
            {
                WriteLogMessage(errorCode, sev, format, args, exception);
            }
        }

        internal bool CheckBulkMessageLimits(int logCode, Severity sev)
        {
            var now = DateTime.UtcNow;
            int count;
            TimeSpan sinceInterval;
            Dictionary<int, int> copyMessageCounts = null;

            bool isExcluded = LogManager.excludedBulkLogCodes.Contains(logCode)
                              || (sev == Severity.Verbose || sev == Severity.Verbose2 || sev == Severity.Verbose3);

            lock (this)
            {
                sinceInterval = now - lastBulkLogMessageFlush;
                if (sinceInterval >= LogManager.BulkMessageInterval)
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
                    int num = copyMessageCounts[ec] - LogManager.BulkMessageLimit;

                    // Only output log codes which exceeded limit threshold
                    if (num > 0)
                    {
                        args[0] = ec;
                        args[1] = num;
                        args[2] = (num == 1) ? "" : "s";

                        WriteLogMessage(ec + LogManager.BulkMessageSummaryOffset, Severity.Info, "Log code {0} occurred {1} additional time{2} in the previous {3}", args, null);
                    }
                }
            }

            // Should the current log message be output?
            return isExcluded || (count <= LogManager.BulkMessageLimit);
        }

        private static string FormatMessageText(string format, object[] args)
        {
            // avoids exceptions if format string contains braces in calls that were not
            // designed to use format strings
            return (args == null || args.Length == 0) ? format : String.Format(format, args);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void WriteLogMessage(int errorCode, Severity sev, string format, object[] args, Exception exception)
        {
            string message = FormatMessageText(format, args);

            bool logMessageTruncated = false;
            if (message.Length > LogManager.MAX_LOG_MESSAGE_SIZE)
            {
                message = $"{message.Substring(0, LogManager.MAX_LOG_MESSAGE_SIZE)}. MESSAGE TRUNCATED AT THIS POINT!! Max message size = {LogManager.MAX_LOG_MESSAGE_SIZE}";
                logMessageTruncated = true;
            }

            foreach (ILogConsumer consumer in LogManager.LogConsumers)
            {
                try
                {
                    consumer.Log(sev, loggerType, Name, message, LogManager.MyIPEndPoint, exception, errorCode);

                    if (logMessageTruncated)
                    {
                        consumer.Log(Severity.Warning, loggerType, Name,
                            "Previous log message was truncated - Max size = " + LogManager.MAX_LOG_MESSAGE_SIZE,
                            LogManager.MyIPEndPoint, exception,
                            (int)ErrorCode.Logger_LogMessageTruncated);
                    }
                }
                catch (Exception exc)
                {
                    Console.WriteLine("Exception while passing a log message to log consumer. Logger type:{0}, name:{1}, severity:{2}, message:{3}, error code:{4}, message exception:{5}, log consumer exception:{6}",
                        consumer.GetType().FullName, Name, sev, message, errorCode, exception, exc);
                }
            }

            var formatedTraceMessage = TraceParserUtils.FormatLogMessage(sev, loggerType, Name, message, LogManager.MyIPEndPoint, exception, errorCode);

            if (exception != null)
                TrackException(exception);

            TrackTrace(formatedTraceMessage, sev);

            if (logMessageTruncated)
            {
                formatedTraceMessage = TraceParserUtils.FormatLogMessage(Severity.Warning, loggerType, Name,
                    "Previous log message was truncated - Max size = " + LogManager.MAX_LOG_MESSAGE_SIZE,
                    LogManager.MyIPEndPoint, exception,
                    (int)ErrorCode.Logger_LogMessageTruncated);

                TrackTrace(formatedTraceMessage);
            }

            if ((DateTime.UtcNow - lastFlush) > flushInterval)
            {
                lastFlush = DateTime.UtcNow;
                LogManager.Flush();
            }
        }

        #region APM Methods

        public override void TrackDependency(string name, string commandName, DateTimeOffset startTime, TimeSpan duration, bool success)
        {
            foreach (var tc in LogManager.TelemetryConsumers.OfType<IDependencyTelemetryConsumer>())
            {
                tc.TrackDependency(name, commandName, startTime, duration, success);
            }
        }

        public override void TrackEvent(string name, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null)
        {
            foreach (var tc in LogManager.TelemetryConsumers.OfType<IEventTelemetryConsumer>())
            {
                tc.TrackEvent(name, properties, metrics);
            }
        }

        public override void TrackMetric(string name, double value, IDictionary<string, string> properties = null)
        {
            foreach (var tc in LogManager.TelemetryConsumers.OfType<IMetricTelemetryConsumer>())
            {
                tc.TrackMetric(name, value, properties);
            }
        }

        public override void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null)
        {
            foreach (var tc in LogManager.TelemetryConsumers.OfType<IMetricTelemetryConsumer>())
            {
                tc.TrackMetric(name, value, properties);
            }
        }

        public override void IncrementMetric(string name)
        {
            foreach (var tc in LogManager.TelemetryConsumers.OfType<IMetricTelemetryConsumer>())
            {
                tc.IncrementMetric(name);
            }
        }

        public override void IncrementMetric(string name, double value)
        {
            foreach (var tc in LogManager.TelemetryConsumers.OfType<IMetricTelemetryConsumer>())
            {
                tc.IncrementMetric(name, value);
            }
        }

        public override void DecrementMetric(string name)
        {
            foreach (var tc in LogManager.TelemetryConsumers.OfType<IMetricTelemetryConsumer>())
            {
                tc.DecrementMetric(name);
            }
        }

        public override void DecrementMetric(string name, double value)
        {
            foreach (var tc in LogManager.TelemetryConsumers.OfType<IMetricTelemetryConsumer>())
            {
                tc.DecrementMetric(name, value);
            }
        }

        public override void TrackRequest(string name, DateTimeOffset startTime, TimeSpan duration, string responseCode, bool success)
        {
            foreach (var tc in LogManager.TelemetryConsumers.OfType<IRequestTelemetryConsumer>())
            {
                tc.TrackRequest(name, startTime, duration, responseCode, success);
            }
        }

        public override void TrackException(Exception exception, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null)
        {
            foreach (var tc in LogManager.TelemetryConsumers.OfType<IExceptionTelemetryConsumer>())
            {
                tc.TrackException(exception, properties, metrics);
            }
        }

        public override void TrackTrace(string message)
        {
            foreach (var tc in LogManager.TelemetryConsumers.OfType<ITraceTelemetryConsumer>())
            {
                tc.TrackTrace(message);
            }
        }

        public override void TrackTrace(string message, Severity severity)
        {
            foreach (var tc in LogManager.TelemetryConsumers.OfType<ITraceTelemetryConsumer>())
            {
                tc.TrackTrace(message, severity);
            }
        }

        public override void TrackTrace(string message, Severity severity, IDictionary<string, string> properties)
        {
            foreach (var tc in LogManager.TelemetryConsumers.OfType<ITraceTelemetryConsumer>())
            {
                tc.TrackTrace(message, severity, properties);
            }
        }

        public override void TrackTrace(string message, IDictionary<string, string> properties)
        {
            foreach (var tc in LogManager.TelemetryConsumers.OfType<ITraceTelemetryConsumer>())
            {
                tc.TrackTrace(message, properties);
            }
        }

        #endregion

    }
}
