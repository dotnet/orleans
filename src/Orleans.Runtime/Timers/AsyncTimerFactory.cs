using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class AsyncTimerFactory : IAsyncTimerFactory
    {
        private readonly ILoggerFactory loggerFactory;
        private readonly TimeProvider timeProvider;

        public AsyncTimerFactory(ILoggerFactory loggerFactory)
            : this(loggerFactory, TimeProvider.System)
        {
        }

        public AsyncTimerFactory(ILoggerFactory loggerFactory, TimeProvider timeProvider)
        {
            this.loggerFactory = loggerFactory;
            this.timeProvider = timeProvider;
        }

        public IAsyncTimer Create(TimeSpan period, string name)
        {
            var log = this.loggerFactory.CreateLogger($"{typeof(AsyncTimer).FullName}.{name}");
            return new AsyncTimer(period, name, log, this.timeProvider);
        }
    }
}
