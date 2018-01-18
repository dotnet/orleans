using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions.Internal;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;

namespace Orleans.Logging.Legacy
{
    /// <summary>
    /// LegacyOrleansLogger supports legacy Orleans logging features, including <see cref="ILogConsumer"/>, <see cref="ICloseableLogConsumer"/>,
    /// <see cref="IFlushableLogConsumer"/>, <see cref="Severity"/>. 
    /// </summary>
    [Obsolete(OrleansLoggingUtils.ObsoleteMessageString)]
    public class LegacyOrleansLogger : ILogger
    {
        private readonly TimeSpan flushInterval = Debugger.IsAttached ? TimeSpan.FromMilliseconds(10) : TimeSpan.FromSeconds(1);
        private DateTime lastFlush = DateTime.UtcNow;

        private readonly IList<ILogConsumer> logConsumers;
        private readonly string name;
        private readonly IList<IFlushableLogConsumer> flushableConsumers;
        private readonly LoggerType loggerType;
        private readonly IPEndPoint ipEndPoint;
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="categoryName">category name for current logger</param>
        /// <param name="logConsumers">log consumers which this logger will log messages to</param>
        /// <param name="ipEndPoint">IP endpoint this logger is associated with</param>
        public LegacyOrleansLogger(string categoryName, IList<ILogConsumer> logConsumers, IPEndPoint ipEndPoint)
        {
            this.logConsumers = logConsumers;
            this.flushableConsumers = logConsumers.OfType<IFlushableLogConsumer>().ToList();
            this.name = categoryName;
            this.loggerType = DetermineLoggerType(categoryName);
            this.ipEndPoint = ipEndPoint;
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
            return true;
        }

        /// <summary>
        /// Log a message. Current logger supports legacy event bulking feature. Message bulking feature will only log eventId code appearance count
        /// if certain event appear more than <see cref="EventBulkingOptions.BulkEventLimit" /> in <see cref="EventBulkingOptions.BulkEventInterval"/>
        /// </summary>
        /// <typeparam name="TState"></typeparam>
        /// <param name="logLevel"></param>
        /// <param name="eventId"></param>
        /// <param name="state"></param>
        /// <param name="exception"></param>
        /// <param name="formatter"></param>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            var errorCode = eventId.Id;
            var severity = LogLevelToSeverity(logLevel);
            WriteLogMessageToLogConsumers(errorCode, severity, formatter(state, exception), exception);
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
                case LogLevel.Critical: return Severity.Error;
                case LogLevel.Error: return Severity.Error;
                case LogLevel.Warning: return Severity.Warning;
                case LogLevel.Information: return Severity.Info;
                case LogLevel.Debug: return Severity.Verbose;
                default: return Severity.Verbose3;
            }
        }

        private LoggerType DetermineLoggerType(string category)
        {
            LoggerType type = LoggerType.Application;
            if (category.Contains("Orleans"))
            {
                type = LoggerType.Runtime;
                if (category.Contains("Provider"))
                    type = LoggerType.Provider;
                if (category.Contains("Grain"))
                    type = LoggerType.Grain;
            }

            return type;
        }

        internal static LogLevel SeverityToLogLevel(Severity severity)
        {
            switch (severity)
            {
                case Severity.Off: return LogLevel.None;
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
                    consumer.Log(sev, this.loggerType, this.name, message, this.ipEndPoint, exception, errorCode);
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
                foreach (var consumer in flushableConsumers)
                {
                    consumer.Flush();
                }
            }
        }
    }
}
