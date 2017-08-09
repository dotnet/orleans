using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Extensions.Logging
{
    /// <summary>
    /// Config for message bulking feature
    /// </summary>
    public class MessageBulkingConfig
    {
        /// <summary>
        /// Count limit for bulk message output.
        /// If the same log code is written more than <c>BulkMessageLimit</c> times in the <c>BulkMessageInterval</c> time period, 
        /// then only the first <c>BulkMessageLimit</c> individual messages will be written, plus a count of how bulk messages suppressed.
        /// </summary>
        public int BulkMessageLimit { get; set; } = DefaultBulkMessageLimit;

        /// <summary>
        /// Default bulk message limit. 
        /// </summary>
        public const int DefaultBulkMessageLimit = Constants.DEFAULT_LOGGER_BULK_MESSAGE_LIMIT;

        /// <summary>
        /// Time limit for bulk message output.
        /// If the same log code is written more than <c>BulkMessageLimit</c> times in the <c>BulkMessageInterval</c> time period, 
        /// then only the first <c>BulkMessageLimit</c> individual messages will be written, plus a count of how bulk messages suppressed.
        /// </summary>
        public TimeSpan BulkMessageInterval { get; set; } = DefaultBulkMessageInterval;

        /// <summary>
        /// Default bulk message interval
        /// </summary>
        public static readonly TimeSpan DefaultBulkMessageInterval = TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// Provides an ILoggerProvider, whose implementation try to preserve orleans legacy logging features and abstraction
    /// OrleansLoggerProvider creates <see cref="OrleansLogger"/>, which supports orleans legacy logging features, including <see cref="ILogConsumer"/>, 
    /// <see cref="ICloseableLogConsumer">, <see cref="IFlushableLogConsumer">, <see cref="Severity">, message bulking. 
    /// OrleansLoggerProvider also supports configuration on those legacy features.
    /// </summary>
    public class OrleansLoggerProvider : ILoggerProvider
    {
        /// <summary>
        /// Default Severity for all loggers
        /// </summary>
        public static Severity DefaultSeverity = Severity.Info;

        private ConcurrentBag<ILogConsumer> logConsumers;
        private OrleansLoggerSeverityOverrides loggerSeverityOverrides;
        private MessageBulkingConfig messageBulkingConfig;

        /// <summary>
        /// Constructor
        /// </summary>
        public OrleansLoggerProvider()
            :this(null, null, null)
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="consumers">Registered log consumers</param>
        public OrleansLoggerProvider(List<ILogConsumer> consumers)
            :this(consumers, null, null)
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="consumers"></param>
        /// <param name="severityOverrides"></param>
        public OrleansLoggerProvider(List<ILogConsumer> consumers, OrleansLoggerSeverityOverrides severityOverrides)
            :this(consumers, severityOverrides, null)
        { }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="consumers">Registered log consumers</param>
        /// <param name="severityOverrides">per logger category Severity overides</param>
        /// <param name="messageBulkingConfig"></param>
        public OrleansLoggerProvider(List<ILogConsumer> consumers, OrleansLoggerSeverityOverrides severityOverrides, MessageBulkingConfig messageBulkingConfig)
        {
            this.logConsumers = new ConcurrentBag<ILogConsumer>();
            consumers?.ForEach(consumer => this.logConsumers.Add(consumer));
            this.loggerSeverityOverrides = severityOverrides == null? new OrleansLoggerSeverityOverrides() : severityOverrides;
            this.messageBulkingConfig = messageBulkingConfig == null ? new MessageBulkingConfig() : messageBulkingConfig;
        }

        /// <summary>
        /// Find severity level for the logger with categoryName
        /// </summary>
        /// <param name="categoryName"></param>
        /// <returns></returns>
        public Severity FindSeverityLevel(string categoryName)
        {
            if (this.loggerSeverityOverrides.LoggerSeverityOverrides.ContainsKey(categoryName))
                return this.loggerSeverityOverrides.LoggerSeverityOverrides[categoryName];
            //if no severity override, return the default severity.
            return DefaultSeverity;
        }
        /// <inheritdoc/>
        public ILogger CreateLogger(string categoryName)
        {
            return new OrleansLogger(categoryName, this.logConsumers.ToArray(), FindSeverityLevel(categoryName), this.messageBulkingConfig);
        }

        /// <summary>
        /// Register log consumer for OrleansLogger to write log message to
        /// </summary>
        /// <param name="logConsumer"></param>
        /// <returns></returns>
        public OrleansLoggerProvider AddLogConsumer(ILogConsumer logConsumer)
        {
            this.logConsumers.Add(logConsumer);
            return this;
        }

        /// <summary>
        /// Add severity overrides
        /// </summary>
        /// <param name="categoryName"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public OrleansLoggerProvider AddSeverityOverrides(string categoryName, Severity value)
        {
            this.loggerSeverityOverrides.AddLoggerSeverityOverrides(categoryName, value);
            return this;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            foreach (var logConsumer in this.logConsumers)
            {
                (logConsumer as ICloseableLogConsumer)?.Close();
            }

            this.logConsumers = null;
            this.loggerSeverityOverrides = null;
            this.messageBulkingConfig = null;
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
        public ConcurrentDictionary<string, Severity> LoggerSeverityOverrides { get; }

        /// <summary>
        /// Constructor
        /// </summary>
        public OrleansLoggerSeverityOverrides()
        {
            this.LoggerSeverityOverrides = new ConcurrentDictionary<string, Severity>();
        }

        /// <summary>
        /// Add a severity override
        /// </summary>
        /// <param name="categoryName"></param>
        /// <param name="loggerSeverity"></param>
        /// <returns></returns>
        public OrleansLoggerSeverityOverrides AddLoggerSeverityOverrides(string categoryName, Severity loggerSeverity)
        {
            this.LoggerSeverityOverrides.AddOrUpdate(categoryName, loggerSeverity, (name, sev) => sev);
            return this;
        }
    }
}
