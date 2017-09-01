using System;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Extensions.Logging
{
    /// <summary>
    /// MessageBulkingLoggerProvider, which has message bulking feature in. If you want to add message bulking feature on top of your logger provider,
    /// you just need to use <see cref="ILoggerFactory.AddBulkLoggerDecorator"/>. 
    /// Note: It need to be a typed class for <see cref="LoggerFilterRule"/> to work, such as per provider type filter
    /// </summary>
    public class MessageBulkingLoggerProvider<TDecoratedLoggerProvider> : ILoggerProvider
        where TDecoratedLoggerProvider : ILoggerProvider
    {
        private readonly ILoggerProvider provider;
        private readonly MessageBulkingConfig bulkingConfig;
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="provider"></param>
        /// <param name="bulkingConfig"></param>
        public MessageBulkingLoggerProvider(TDecoratedLoggerProvider provider, MessageBulkingConfig bulkingConfig)
        {
            if(provider == null)
                throw new ArgumentException(nameof(provider));
            this.provider = provider;
            this.bulkingConfig = bulkingConfig == null? new MessageBulkingConfig() : bulkingConfig;
        }

        /// <inheritdoc cref="ILoggerProvider"/>
        public ILogger CreateLogger(string categoryName)
        {
            return new OrleansLoggingDecorator(this.bulkingConfig, provider.CreateLogger(categoryName));
        }

        /// <inheritdoc cref="IDisposable"/>
        public void Dispose()
        {
            provider.Dispose();
        }
    }
}
