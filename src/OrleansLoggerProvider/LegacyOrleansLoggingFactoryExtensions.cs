using System;
using System.Collections.Generic;
using System.Net;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Extensions.Logging.Legacy
{
    public static class LegacyOrleansLoggingFactoryExtensions
    {
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