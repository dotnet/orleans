using System;
using System.Collections.Generic;
using System.Net;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Logging.Legacy
{
    public static class LegacyOrleansLoggingFactoryExtensions
    {
        /// <summary>
        /// Add <see cref="LegacyOrleansLoggerProvider"/> with event bulking feature to LoggerFactory
        /// </summary>
        /// <param name="builder">logger builder</param>
        /// <param name="logConsumers">log consumers which user want to write log events to</param>
        /// <param name="ipEndPoint">IP endpoint this logger is associated with</param>
        /// <param name="eventBulkingOptions">config for event bulking feature</param>
        /// <returns></returns>
        [Obsolete(OrleansLoggingUtils.ObsoleteMessageString)]
        public static ILoggingBuilder AddLegacyOrleansLogging(
            this ILoggingBuilder builder,
            IEnumerable<ILogConsumer> logConsumers,
            IPEndPoint ipEndPoint = null,
            EventBulkingOptions eventBulkingOptions = null)
        {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.AddMessageBulkingLoggerProvider<LegacyOrleansLoggerProvider>(new LegacyOrleansLoggerProvider(logConsumers, ipEndPoint), eventBulkingOptions);
            return builder;
        }

        /// <summary>
        ///Add <see cref="LegacyOrleansLoggerProvider"/> with event bulking feature to LoggerFactory
        /// </summary>
        /// <param name="builder">logger builder</param>
        /// <param name="logConsumers">log consumers which configured to consume the logs</param>
        /// <param name="severityOverrides">per category severity overrides</param>
        /// <param name="ipEndPoint">IP endpoint this logger is associated with</param>
        /// <param name="eventBulkingOptions">config for event bulking feature</param>
        /// <returns></returns>
        [Obsolete(OrleansLoggingUtils.ObsoleteMessageString)]
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
            builder.AddMessageBulkingLoggerProvider<LegacyOrleansLoggerProvider>(new LegacyOrleansLoggerProvider(logConsumers, ipEndPoint), eventBulkingOptions);
            return builder;
        }

        /// <summary>
        /// Add event bulking feature onto <paramref name="provider"/>, and add that new logger provider 
        /// <see cref="EventBulkingLoggerProvider{TDecoratedLoggerProvider}"/> into <paramref name="builder"/>.
        /// </summary>
        /// <param name="builder">logger builder</param>
        /// <param name="provider">logger provider</param>
        /// <param name="eventBulkingOptions">options for event bulking feature</param>
        /// <returns></returns>
        public static ILoggingBuilder AddMessageBulkingLoggerProvider<TDecoratedLoggerProvider>(this ILoggingBuilder builder, TDecoratedLoggerProvider provider, EventBulkingOptions eventBulkingOptions = null)
            where TDecoratedLoggerProvider : ILoggerProvider
        {
            builder.AddProvider(new EventBulkingLoggerProvider<TDecoratedLoggerProvider>(provider, eventBulkingOptions));
            return builder;
        }

    }
}