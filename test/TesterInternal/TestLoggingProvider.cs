using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions.Internal;

namespace UnitTests.General
{
    public class TestLoggingProvider : ILoggerProvider
    {
        private readonly TestLogger logger;

        public TestLoggingProvider(Action<string> logMethod)
        {
            this.logger = new TestLogger(logMethod);
        }
        
        private class TestLogger : ILogger
        {
            private readonly Action<string> logMethod;

            public TestLogger(Action<string> logMethod)
            {
                this.logMethod = logMethod;
            }

            void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            {
                this.logMethod($"{logLevel}: ({eventId}-{eventId})-{formatter(state, exception)}");
            }

            bool ILogger.IsEnabled(LogLevel logLevel) => true;

            IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;

        }
        
        public void Dispose()
        {
        }

        public ILogger CreateLogger(string categoryName)
        {
            return this.logger;
        }
    }
}