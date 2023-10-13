using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace TestExtensions
{
    public class XunitLoggerProvider : ILoggerProvider
    {
        private readonly ITestOutputHelper output;

        public XunitLoggerProvider(ITestOutputHelper output)
        {
            this.output = output;
        }

        public ILogger CreateLogger(string categoryName) => new XunitLogger(this.output, categoryName);

        public void Dispose()
        {
        }

        private class XunitLogger : ILogger, IDisposable
        {
            private readonly ITestOutputHelper output;
            private readonly string category;

            public XunitLogger(ITestOutputHelper output, string category)
            {
                this.output = output;
                this.category = category;
            }
            
            public IDisposable BeginScope<TState>(TState state) => this;

            public void Dispose() { }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                this.output.WriteLine($"{logLevel} [{this.category}.{eventId.Name ?? eventId.Id.ToString()}] {formatter(state, exception)}");
            }
        }
    }
}
