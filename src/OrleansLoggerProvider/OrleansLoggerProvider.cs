﻿using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace Orleans.Extensions.Logging
{
    /// <summary>
    /// Provides an ILoggerProvider, whose implementation try to preserve orleans legacy logging features and abstraction
    /// OrleansLoggerProvider creates one ILogger implementation, which supports orleans legacy logging features, including <see cref="ILogConsumer"/>, 
    /// <see cref="ICloseableLogConsumer">, <see cref="IFlushableLogConsumer">, <see cref="Severity">. 
    /// OrleansLoggerProvider also supports configuration on those legacy features.
    /// </summary>
    [Obsolete]
    public class OrleansLoggerProvider : ILoggerProvider
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
        public OrleansLoggerProvider()
            :this(null, null)
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="consumers">Registered log consumers</param>
        /// <param name="severityOverrides">per logger category Severity overides</param>
        /// <param name="messageBulkingConfig"></param>
        public OrleansLoggerProvider(List<ILogConsumer> consumers, IPEndPoint ipEndPoint)
        {
            this.LogConsumers = new ConcurrentBag<ILogConsumer>();
            this.ipEndPoint = ipEndPoint;
            consumers?.ForEach(consumer => this.LogConsumers.Add(consumer));
        }

        /// <inheritdoc/>
        public ILogger CreateLogger(string categoryName)
        {
            return new OrleansLogger(categoryName, this.LogConsumers.ToArray(), ipEndPoint);
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
    [Obsolete]
    public class OrleansLoggerSeverityOverrides
    {
        /// <summary>
        /// LoggerSeverityOverrides, which key being logger category name, value being its overrided severity
        /// </summary>
        public ConcurrentDictionary<string, Severity> LoggerSeverityOverrides { get; }

        /// <summary>
        /// Constructor
        /// </summary>
        public OrleansLoggerSeverityOverrides()
        {
            this.LoggerSeverityOverrides = new ConcurrentDictionary<string, Severity>();
        }
    }
}
