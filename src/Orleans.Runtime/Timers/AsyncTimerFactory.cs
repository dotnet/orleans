using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class AsyncTimerFactory : IAsyncTimerFactory
    {
        private readonly ILoggerFactory loggerFactory;
        private readonly TimeProvider _timeProvider;

        public AsyncTimerFactory(ILoggerFactory loggerFactory, [FromKeyedServices(TimeProviderNames.SystemTimers)] TimeProvider timeProvider)
        {
            this.loggerFactory = loggerFactory;
            _timeProvider = timeProvider;
        }

        public IAsyncTimer Create(TimeSpan period, string name)
        {
            var log = this.loggerFactory.CreateLogger($"{typeof(AsyncTimer).FullName}.{name}");
            return new AsyncTimer(period, name, log, _timeProvider);
        }
    }
}
