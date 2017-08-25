using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Extensions.Logging
{
    public static class OrleansLoggingFactoryExtension
    {
        /// <summary>
        /// Add <see cref="OrleansLoggerProvider"> to LoggerFactory
        /// </summary>
        /// <param name="factory"></param>
        /// <param name="logConsumers">log consumers which user want to write log events to</param>
        /// <param name="messageBulkingConfig">config for message bulking feature</param>
        /// <returns></returns>
        [Obsolete]
        public static ILoggerFactory AddOrleansLogging(
            this ILoggerFactory factory,
            List<ILogConsumer> logConsumers,
            MessageBulkingConfig messageBulkingConfig = null)
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));
            factory.AddProvider(new OrleansLoggerProvider(logConsumers, new OrleansLoggerSeverityOverrides(), messageBulkingConfig));
            return factory;
        }

        /// <summary>
        /// Add orleans logging
        /// </summary>
        /// <param name="factory"></param>
        /// <param name="logConsumers"></param>
        /// <param name="severityOverrides"></param>
        /// <param name="messageBulkingConfig">config for message bulking feature</param>
        /// <returns></returns>
        [Obsolete]
        public static ILoggerFactory AddOrleansLogging(
            this ILoggerFactory factory,
            List<ILogConsumer> logConsumers,
            OrleansLoggerSeverityOverrides severityOverrides,
            MessageBulkingConfig messageBulkingConfig = null)
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));
            factory.AddProvider(new OrleansLoggerProvider(logConsumers, severityOverrides, messageBulkingConfig));
            return factory;
        }
    }
    
}
