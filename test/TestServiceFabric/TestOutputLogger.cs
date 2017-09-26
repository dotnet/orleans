using System;
using Orleans.Runtime;
using Xunit.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions.Internal;
namespace TestServiceFabric
{
    public class TestOutputLogger : ILogger
    {

        public string LoggerName { get; set; }

        public string Name => this.LoggerName;
        private LogLevel logLevel;
        public TestOutputLogger(ITestOutputHelper output, string name = null, LogLevel level = LogLevel.Information)
        {
            this.Output = output;
            this.LoggerName = name ?? nameof(TestOutputLogger);
            this.logLevel = level;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return NullScope.Instance;
        }

        public ITestOutputHelper Output { get; set; }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel > this.logLevel;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            this.Output.WriteLine($"{logLevel} {eventId} [{this.Name}] {formatter(state, exception)}");
        }

    }

    public class TestOutputLoggerProvider : ILoggerProvider
    {
        private ITestOutputHelper output;
        public TestOutputLoggerProvider(ITestOutputHelper output)
        {
            this.output = output;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new TestOutputLogger(this.output, categoryName);
        }

        public void Dispose()
        {
            output = null;
        }
    }

}