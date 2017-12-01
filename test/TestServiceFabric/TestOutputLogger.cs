using System;
using Xunit.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions.Internal;
namespace TestServiceFabric
{
    public class TestOutputLogger<TCategoryName> : TestOutputLogger, ILogger<TCategoryName>
    {
        public TestOutputLogger(ITestOutputHelper output, LogLevel level = LogLevel.Information) : base(output, typeof(TCategoryName).FullName, level)
        {
        }
    }

    public class TestOutputLogger : ILogger
    {
        private readonly LogLevel minLevel;

        public TestOutputLogger(ITestOutputHelper output, string name = null, LogLevel level = LogLevel.Information)
        {
            this.Output = output;
            this.Name = name ?? nameof(TestOutputLogger);
            this.minLevel = level;
        }

        public string Name { get; }

        public IDisposable BeginScope<TState>(TState state)
        {
            return NullScope.Instance;
        }

        public ITestOutputHelper Output { get; set; }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel > this.minLevel;
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