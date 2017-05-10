using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Aggregated cache pressure monitor
    /// </summary>
    public class AggregatedCachePressureMonitor : List<ICachePressureMonitor>, ICachePressureMonitor
    {
        private bool isUnderPressure;
        private Logger logger;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="logger"></param>
        public AggregatedCachePressureMonitor(Logger logger)
        {
            this.isUnderPressure = false;
            this.logger = logger.GetSubLogger(this.GetType().Name);
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
        /// If any mornitor in this aggregated cache monitor group is under pressure, then return true
        /// </summary>
        /// <param name="utcNow"></param>
        /// <returns></returns>
        public bool IsUnderPressure(DateTime utcNow)
        {
            bool underPressure = this.Any(monitor => monitor.IsUnderPressure(utcNow));
            if (this.isUnderPressure != underPressure)
            {
                this.isUnderPressure = underPressure;
                logger.Info(this.isUnderPressure
                    ? $"Ingesting messages too fast. Throttling message reading."
                    : $"Message ingestion is healthy.");
            }
            return underPressure;
        }
    }
}
