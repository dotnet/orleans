using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace OneBoxDeployment.IntegrationTests
{
    /// <summary>
    /// A structured log message in the system
    /// </summary>
    [DebuggerDisplay("LogMesage(EventId = {EventId}, Message = {Message})")]
    public sealed class LogMessage
    {
        /// <summary>
        /// The log level.
        /// </summary>
        public LogLevel Type { get; set; }

        /// <summary>
        /// The time of logging.
        /// </summary>
        public DateTimeOffset Timestamp { get; set; }

        /// <summary>
        /// The log message.
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// The log catetory.
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// The log event identifier.
        /// </summary>
        public int EventId { get; set; }
    }


    //TODO: Add XUnit too and/or use https://github.com/aspnet/Extensions/blob/f162f1006bf8954f0102af8ff98c04077cf21b04/src/Logging/Logging.Testing/src/XunitLoggerProvider.cs?

    /// <summary>
    /// A logger that writes messages to a an in-memory list.
    /// </summary>
    public sealed class InMemoryLoggerProvider: IDisposable, ILoggerProvider
    {
        /// <summary>
        /// A lock object for log messages.
        /// </summary>
        private readonly object logLock = new object();

        /// <summary>
        /// The messages stored while logging.
        /// </summary>
        private List<LogMessage> Messages { get; } = new List<LogMessage>();

        /// <inheritdoc />
        public void Dispose() { }


        /// <summary>
        /// The messages stored while logging.
        /// </summary>
        public List<LogMessage> LogMessages
        {
            get
            {
                lock(logLock)
                {
                    return new List<LogMessage>(Messages);
                }
            }
        }


        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => new InMemoryLogger(this, categoryName);


        /// <summary>
        /// Logs a message.
        /// </summary>
        /// <typeparam name="TState">The log state type.</typeparam>
        /// <param name="categoryName">The log category.</param>
        /// <param name="logLevel">The log level.</param>
        /// <param name="eventId">The log event identifier.</param>
        /// <param name="state">The log state.</param>
        /// <param name="exception">An exception if any.</param>
        /// <param name="formatter">The exception formatter.</param>
        private void Log<TState>(string categoryName, LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            var message = new LogMessage
            {
                Type = logLevel,
                Timestamp = DateTimeOffset.UtcNow,
                Message = formatter(state, exception) + (exception == null ? string.Empty : Environment.NewLine + exception),
                Category = categoryName,
                EventId = eventId.Id,
            };

            lock(logLock)
            {
                Messages.Add(message);
            }
        }


        /// <summary>
        /// The actual logging implementation.
        /// </summary>
        private sealed class InMemoryLogger: ILogger
        {
            /// <summary>
            /// The logger provider.
            /// </summary>
            private InMemoryLoggerProvider LoggerProvider { get; }

            /// <summary>
            /// The logging category name.
            /// </summary>
            private string CategoryName { get; }


            /// <summary>
            /// A default constructor.
            /// </summary>
            /// <param name="provider">The provider name.</param>
            /// <param name="categoryName">The category name.</param>
            public InMemoryLogger(InMemoryLoggerProvider provider, string categoryName)
            {
                LoggerProvider = provider ?? throw new ArgumentNullException(nameof(provider));

                if(string.IsNullOrWhiteSpace(categoryName))
                {
                    throw new ArgumentException("The category name must contain at least one non-whitespace character.", nameof(categoryName));
                }

                CategoryName = categoryName;
            }


            /// <summary>
            /// Log message.
            /// </summary>
            /// <typeparam name="TState"The log state type.></typeparam>
            /// <param name="logLevel">The log level.</param>
            /// <param name="eventId">The event identifier.</param>
            /// <param name="state">The logging state.</param>
            /// <param name="exception">The exception.</param>
            /// <param name="formatter">The log formatter.</param>
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception exception,
                Func<TState, Exception,
                    string> formatter)
                => LoggerProvider.Log(CategoryName, logLevel, eventId, state, exception, formatter);


            /// <summary>
            /// If this logger is enabled or not.
            /// </summary>
            /// <param name="logLevel"></param>
            /// <returns></returns>
            public bool IsEnabled(LogLevel logLevel) => true;

            /// <summary>
            /// Scope dispose.
            /// </summary>
            /// <typeparam name="TState">The scope state type.</typeparam>
            /// <param name="state">The scope state.</param>
            /// <returns>The disposable of this logger.</returns>
            public IDisposable BeginScope<TState>(TState state) => NoopDisposable.Instance;

            /// <summary>
            /// A private no-operation scope disposable.
            /// </summary>
            private class NoopDisposable: IDisposable
            {
                /// <summary>
                /// A singleton for no-operation disposable.
                /// </summary>
                public static NoopDisposable Instance = new NoopDisposable();

                /// <inheritdoc />
                public void Dispose() { }
            }
        }
    }
}
