﻿using System;
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
        /// <param name="builder">logger builder</param>
        /// <param name="logConsumers">log consumers which user want to write log events to</param>
        /// <param name="eventBulkingOptions">config for event bulking feature</param>
        /// <returns></returns>
        [Obsolete("The Microsoft.Orleans.Logging.Legacy namespace was kept to facilitate migration from Orleans 1.x but will be removed in the near future. It is recommended that you use the Microsoft.Extensions.Logging infrastructure and providers directly instead of Microsoft.Orleans.Logging.Legacy.Logger and Microsoft.Orleans.Logging.Legacy.ILogConsumer")]
        public static ILoggingBuilder AddLegacyOrleansLogging(
            this ILoggingBuilder builder,
            IEnumerable<ILogConsumer> logConsumers,
            IPEndPoint ipEndPoint = null,
            EventBulkingOptions eventBulkingOptions = null)
        {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.AddMesageBulkingLoggerProvider<LegacyOrleansLoggerProvider>(new LegacyOrleansLoggerProvider(logConsumers, ipEndPoint), eventBulkingOptions);
            return builder;
        }

        /// <summary>
        ///Add <see cref="LegacyOrleansLoggerProvider"> with event bulking feature to LoggerFactory
        /// </summary>
        /// <param name="builder">logger builder</param>
        /// <param name="logConsumers">log consumers which configured to consume the logs</param>
        /// <param name="severityOverrides">per category severity overrides</param>
        /// <param name="eventBulkingOptions">config for event bulking feature</param>
        /// <returns></returns>
        [Obsolete("The Microsoft.Orleans.Logging.Legacy namespace was kept to facilitate migration from Orleans 1.x but will be removed in the near future. It is recommended that you use the Microsoft.Extensions.Logging infrastructure and providers directly instead of Microsoft.Orleans.Logging.Legacy.Logger and Microsoft.Orleans.Logging.Legacy.ILogConsumer")]
        public static ILoggingBuilder AddLegacyOrleansLogging(
            this ILoggingBuilder builder,
            IEnumerable<ILogConsumer> logConsumers,
            OrleansLoggerSeverityOverrides severityOverrides,
            IPEndPoint ipEndPoint = null,
            EventBulkingOptions eventBulkingOptions = null)
        {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            foreach (var severityOverride in severityOverrides.LoggerSeverityOverrides)
            {
                builder.AddFilter<EventBulkingLoggerProvider<LegacyOrleansLoggerProvider>>(severityOverride.Key, LegacyOrleansLogger.SeverityToLogLevel(severityOverride.Value));
            }
            builder.AddMesageBulkingLoggerProvider<LegacyOrleansLoggerProvider>(new LegacyOrleansLoggerProvider(logConsumers, ipEndPoint), eventBulkingOptions);
            return builder;
        }

        /// <summary>
        /// Add event bulking feature onto <param name="provider"></param>, and add that new logger provider 
        /// <see cref="EventBulkingLoggerProvider{TDecoratedLoggerProvider}"/> into <param name="builder"></param>.
        /// </summary>
        /// <param name="builder">logger builder</param>
        /// <param name="provider">logger provider</param>
        /// <param name="eventBulkingOptions">options for event bulking feature</param>
        /// <returns></returns>
        public static ILoggingBuilder AddMesageBulkingLoggerProvider<TDecoratedLoggerProvider>(this ILoggingBuilder builder, TDecoratedLoggerProvider provider, EventBulkingOptions eventBulkingOptions = null)
            where TDecoratedLoggerProvider : ILoggerProvider
        {
            builder.AddProvider(new EventBulkingLoggerProvider<TDecoratedLoggerProvider>(provider, eventBulkingOptions));
            return builder;
        }

    }
}