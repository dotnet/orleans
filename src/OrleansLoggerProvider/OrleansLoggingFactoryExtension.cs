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
        /// Add message bulking feature onto <param name="provider"></param>, and add that new logger provider 
        /// <see cref="MessageBulkingLoggerProvider{TDecoratedLoggerProvider}"/> into <param name="builder"></param>.
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="provider"></param>
        /// <param name="messageBulkingConfig"></param>
        /// <returns></returns>
        public static ILoggingBuilder AddMesageBulkingLoggerProvider<TDecoratedLoggerProvider>(this ILoggingBuilder builder, TDecoratedLoggerProvider provider, MessageBulkingConfig messageBulkingConfig)
            where TDecoratedLoggerProvider : ILoggerProvider
        {
            builder.AddProvider(new MessageBulkingLoggerProvider<TDecoratedLoggerProvider>(provider, messageBulkingConfig));
            return builder;
        }

        /// <summary>
        /// Add <see cref="OrleansLoggerProvider"> with message bulking feature to LoggerFactory
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="logConsumers">log consumers which user want to write log events to</param>
        /// <param name="messageBulkingConfig">config for message bulking feature</param>
        /// <returns></returns>
        [Obsolete]
        public static ILoggingBuilder AddOrleansLogging(
            this ILoggingBuilder builder,
            List<ILogConsumer> logConsumers,
            IPEndPoint ipEndPoint = null,
            MessageBulkingConfig messageBulkingConfig = null)
        {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.AddMesageBulkingLoggerProvider<OrleansLoggerProvider>(new OrleansLoggerProvider(logConsumers, ipEndPoint), messageBulkingConfig);
            return builder;
        }

        /// <summary>
        ///Add <see cref="OrleansLoggerProvider"> with message bulking feature to LoggerFactory
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="logConsumers"></param>
        /// <param name="severityOverrides"></param>
        /// <param name="messageBulkingConfig">config for message bulking feature</param>
        /// <returns></returns>
        [Obsolete]
        public static ILoggingBuilder AddOrleansLogging(
            this ILoggingBuilder builder,
            List<ILogConsumer> logConsumers,
            OrleansLoggerSeverityOverrides severityOverrides,
            IPEndPoint ipEndPoint = null,
            MessageBulkingConfig messageBulkingConfig = null)
        {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            foreach (var severityOverride in severityOverrides.LoggerSeverityOverrides)
            {
                builder.AddFilter<MessageBulkingLoggerProvider<OrleansLoggerProvider>>(severityOverride.Key, OrleansLogger.SeverityToLogLevel(severityOverride.Value));
            }
            builder.AddMesageBulkingLoggerProvider<OrleansLoggerProvider>(new OrleansLoggerProvider(logConsumers, ipEndPoint), messageBulkingConfig);
            return builder;
        }
    }
    
}
