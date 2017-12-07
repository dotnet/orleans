using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Logging.Legacy
{
    /// <summary>
    /// EventBulkingLoggerProvider, which has event bulking feature in. If you want to add event bulking feature on top of your logger provider,
    /// you just need to use <see cref="LegacyOrleansLoggingFactoryExtensions.AddMessageBulkingLoggerProvider{TDecoratedLoggerProvider}(ILoggingBuilder, TDecoratedLoggerProvider, EventBulkingOptions)"/>. 
    /// Note: It need to be a typed class for <see cref="LoggerFilterRule"/> to work, such as per provider type filter
    /// </summary>
    public class EventBulkingLoggerProvider<TDecoratedLoggerProvider> : ILoggerProvider
        where TDecoratedLoggerProvider : ILoggerProvider
    {
        private readonly ILoggerProvider provider;
        private readonly EventBulkingOptions bulkingConfig;
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="provider"></param>
        /// <param name="bulkingConfig"></param>
        public EventBulkingLoggerProvider(TDecoratedLoggerProvider provider, EventBulkingOptions bulkingConfig)
        {
            if(provider == null)
                throw new ArgumentException(nameof(provider));
            this.provider = provider;
            this.bulkingConfig = bulkingConfig == null? new EventBulkingOptions() : bulkingConfig;
        }

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName)
        {
            return new EventBulkingDecoratorLogger(this.bulkingConfig, provider.CreateLogger(categoryName));
        }

        /// <inheritdoc />
        public void Dispose()
        {
            provider.Dispose();
        }
    }
}
