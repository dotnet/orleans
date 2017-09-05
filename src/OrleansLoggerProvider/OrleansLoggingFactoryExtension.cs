using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Net;

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

        /// <summary>
        /// Add <see cref="LegacyOrleansLoggerProvider"> with event bulking feature to LoggerFactory
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="logConsumers">log consumers which user want to write log events to</param>
        /// <param name="eventBulkingConfig">config for event bulking feature</param>
        /// <returns></returns>
        [Obsolete("Use Microsoft.Extensions.Logging built-in logger providers")]
        public static ILoggingBuilder AddLegacyOrleansLogging(
            this ILoggingBuilder builder,
            List<ILogConsumer> logConsumers,
            IPEndPoint ipEndPoint = null,
            EventBulkingConfig eventBulkingConfig = null)
        {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.AddMesageBulkingLoggerProvider<LegacyOrleansLoggerProvider>(new LegacyOrleansLoggerProvider(logConsumers, ipEndPoint), eventBulkingConfig);
            return builder;
        }

        /// <summary>
        ///Add <see cref="LegacyOrleansLoggerProvider"> with event bulking feature to LoggerFactory
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="logConsumers"></param>
        /// <param name="severityOverrides"></param>
        /// <param name="eventBulkingConfig">config for event bulking feature</param>
        /// <returns></returns>
        [Obsolete("Use Microsoft.Extensions.Logging built-in logger providers")]
        public static ILoggingBuilder AddLegacyOrleansLogging(
            this ILoggingBuilder builder,
            List<ILogConsumer> logConsumers,
            OrleansLoggerSeverityOverrides severityOverrides,
            IPEndPoint ipEndPoint = null,
            EventBulkingConfig eventBulkingConfig = null)
        {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            foreach (var severityOverride in severityOverrides.LoggerSeverityOverrides)
            {
                builder.AddFilter<EventBulkingLoggerProvider<LegacyOrleansLoggerProvider>>(severityOverride.Key, LegacyOrleansLogger.SeverityToLogLevel(severityOverride.Value));
            }
            builder.AddMesageBulkingLoggerProvider<LegacyOrleansLoggerProvider>(new LegacyOrleansLoggerProvider(logConsumers, ipEndPoint), eventBulkingConfig);
            return builder;
        }
    }
    
}
