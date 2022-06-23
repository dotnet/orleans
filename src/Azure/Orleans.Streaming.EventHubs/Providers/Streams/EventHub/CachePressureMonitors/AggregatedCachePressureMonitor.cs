using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Aggregated cache pressure monitor
    /// </summary>
    public class AggregatedCachePressureMonitor : List<ICachePressureMonitor>, ICachePressureMonitor
    {
        private bool isUnderPressure;
        private ILogger logger;
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
            bool underPressure = this.Any(monitor => monitor.IsUnderPressure(utcNow));
            if (this.isUnderPressure != underPressure)
            {
                this.isUnderPressure = underPressure;
                this.CacheMonitor?.TrackCachePressureMonitorStatusChange(this.GetType().Name, this.isUnderPressure, null, null, null);
                logger.LogInformation(
                    this.isUnderPressure
                    ? "Ingesting messages too fast. Throttling message reading."
                    : "Message ingestion is healthy.");
            }
            return underPressure;
        }
    }
}
