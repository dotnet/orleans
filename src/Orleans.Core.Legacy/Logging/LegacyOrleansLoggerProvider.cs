using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace Orleans.Logging.Legacy
{
    /// <summary>
    /// Provides an ILoggerProvider, whose implementation try to preserve orleans legacy logging features and abstraction
    /// OrleansLoggerProvider creates one ILogger implementation, which supports orleans legacy logging features, including <see cref="ILogConsumer"/>, 
    /// <see cref="ICloseableLogConsumer"/>, <see cref="IFlushableLogConsumer"/>, <see cref="Severity"/>. 
    /// LegacyOrleansLoggerProvider also supports configuration on those legacy features.
    /// </summary>
    public class LegacyOrleansLoggerProvider : ILoggerProvider
    {
        /// <summary>
        /// Default Severity for all loggers
        /// </summary>
        public static Severity DefaultSeverity = Severity.Info;

        public ConcurrentBag<ILogConsumer> LogConsumers { get; private set; }
        private readonly IPEndPoint ipEndPoint;
        /// <summary>
        /// Constructor
        /// </summary>
        public LegacyOrleansLoggerProvider()
            :this(null, null)
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="consumers">Registered log consumers</param>
        /// <param name="ipEndPoint">IP endpoint</param>
        public LegacyOrleansLoggerProvider(IEnumerable<ILogConsumer> consumers, IPEndPoint ipEndPoint)
        {
            this.LogConsumers = new ConcurrentBag<ILogConsumer>();
            this.ipEndPoint = ipEndPoint;
            if (consumers != null)
            {
                foreach (var consumer in consumers)
                {
                    this.LogConsumers.Add(consumer);
                }
            }
        }

        /// <inheritdoc/>
        public ILogger CreateLogger(string categoryName)
        {
            return new LegacyOrleansLogger(categoryName, this.LogConsumers.ToArray(), ipEndPoint);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            foreach (var logConsumer in this.LogConsumers.OfType<ICloseableLogConsumer>())
            {
                logConsumer.Close();
            }

            this.LogConsumers = null;
        }
    }

    /// <summary>
    /// Orleans severity overrides on a per logger base
    /// </summary>
    public class OrleansLoggerSeverityOverrides
    {
        /// <summary>
        /// LoggerSeverityOverrides, which key being logger category name, value being its overrided severity
        /// </summary>
        public Dictionary<string, Severity> LoggerSeverityOverrides { get; set; } = new Dictionary<string, Severity>();
    }
}
