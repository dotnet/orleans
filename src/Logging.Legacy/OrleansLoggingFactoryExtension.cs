using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Extensions.Logging
{
    public static class OrleansLoggingFactoryExtension
    {
        /// <summary>
        /// Add event bulking feature onto <param name="provider"></param>, and add that new logger provider 
        /// <see cref="EventBulkingLoggerProvider{TDecoratedLoggerProvider}"/> into <param name="builder"></param>.
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="provider"></param>
        /// <param name="eventBulkingConfig"></param>
        /// <returns></returns>
        public static ILoggingBuilder AddMesageBulkingLoggerProvider<TDecoratedLoggerProvider>(this ILoggingBuilder builder, TDecoratedLoggerProvider provider, EventBulkingConfig eventBulkingConfig = null)
            where TDecoratedLoggerProvider : ILoggerProvider
        {
            builder.AddProvider(new EventBulkingLoggerProvider<TDecoratedLoggerProvider>(provider, eventBulkingConfig));
            return builder;
        }
    }
    
}
