using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class AsyncTimerFactory : IAsyncTimerFactory
    {
        private readonly ILoggerFactory loggerFactory;
        public AsyncTimerFactory(ILoggerFactory loggerFactory)
        {
            this.loggerFactory = loggerFactory;
        }

        public IAsyncTimer Create(TimeSpan period, string name)
        {
            var log = this.loggerFactory.CreateLogger($"{typeof(AsyncTimer).FullName}.{name}");
            return new AsyncTimer(period, log);
        }
    }
}
