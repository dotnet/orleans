using Orleans.Providers.Streams.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Orleans.Streaming.EventHubs
{
    /// <summary>
    /// Aggregated cache pressure monitor
    /// </summary>
    public partial class AggregatedCachePressureMonitor : List<ICachePressureMonitor>, ICachePressureMonitor
    {
        private bool isUnderPressure;
        private readonly ILogger logger;
        /// <summary>
        /// Cache monitor which is used to report cache related metrics
        /// </summary>
        public ICacheMonitor CacheMonitor { set; private get; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="monitor"></param>
        public AggregatedCachePressureMonitor(ILogger logger, ICacheMonitor monitor = null)
        {
            this.isUnderPressure = false;
            this.logger = logger;
            this.CacheMonitor = monitor;
        }

        /// <summary>
        /// Record cache pressure to every monitor in this aggregated cache monitor group
        /// </summary>
        /// <param name="cachePressureContribution"></param>
        public void RecordCachePressureContribution(double cachePressureContribution)
        {
            this.ForEach(monitor =>
            {
                monitor.RecordCachePressureContribution(cachePressureContribution);
            });
        }

        /// <summary>
        /// Add one monitor to this aggregated cache monitor group
        /// </summary>
        /// <param name="monitor"></param>
        public void AddCachePressureMonitor(ICachePressureMonitor monitor)
        {
            this.Add(monitor);
        }

        /// <summary>
        /// If any monitor in this aggregated cache monitor group is under pressure, then return true
        /// </summary>
        /// <param name="utcNow"></param>
        /// <returns></returns>
        public bool IsUnderPressure(DateTime utcNow)
        {
            bool underPressure = this.Exists(monitor => monitor.IsUnderPressure(utcNow));
            if (this.isUnderPressure != underPressure)
            {
                this.isUnderPressure = underPressure;
                this.CacheMonitor?.TrackCachePressureMonitorStatusChange(this.GetType().Name, this.isUnderPressure, null, null, null);
                if (this.isUnderPressure)
                {
                    LogInfoIngestingMessagesTooFast();
                }
                else
                {
                    LogInfoMessageIngestionIsHealthy();
                }
            }
            return underPressure;
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Ingesting messages too fast. Throttling message reading."
        )]
        private partial void LogInfoIngestingMessagesTooFast();

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Message ingestion is healthy."
        )]
        private partial void LogInfoMessageIngestionIsHealthy();
    }
}
