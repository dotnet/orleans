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
            var log = this.loggerFactory.CreateLogger($"{typeof(AsyncTimer).FullName}.{name}"); // dynamic data (although arguably used staticly), as the name is always nameof(sometype)
            return new AsyncTimer(period, log);
        }
    }
}
