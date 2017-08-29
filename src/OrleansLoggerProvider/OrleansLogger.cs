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
    /// <see cref="IFlushableLogConsumer">, <see cref="Severity">. 
    /// </summary>
    [Obsolete]
    public class OrleansLogger : ILogger
    {
        private readonly TimeSpan flushInterval = Debugger.IsAttached ? TimeSpan.FromMilliseconds(10) : TimeSpan.FromSeconds(1);
        private DateTime lastFlush = DateTime.UtcNow;

        private IList<ILogConsumer> logConsumers;
        private Severity maxSeverityLevel;
        private string name;
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="categoryName"></param>
        /// <param name="logConsumers"></param>
        /// <param name="maxSeverityLevel"></param>
        public OrleansLogger(string categoryName, IList<ILogConsumer> logConsumers, Severity maxSeverityLevel)
        {
            this.logConsumers = logConsumers;
            this.maxSeverityLevel = maxSeverityLevel;
            this.name = categoryName;
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
    }
}
