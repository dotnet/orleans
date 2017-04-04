using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Cache pressure monitor whose back pressure algorithm is based on averaging pressure value
    /// over all pressure contribution
    /// </summary>
    public class AveragingCachePressureMonitor : ICachePressureMonitor
    {
        /// <summary>
        /// Default flow control threshold
        /// </summary>
        public static readonly double DefaultThreshold = 1 / 3;
        private static readonly TimeSpan checkPeriod = TimeSpan.FromSeconds(2);
        private readonly Logger logger;

        private double accumulatedCachePressure;
        private double cachePressureContributionCount;
        private DateTime nextCheckedTime;
        private bool isUnderPressure;
        private double flowControlThreshold;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="logger"></param>
        public AveragingCachePressureMonitor(Logger logger)
            :this(DefaultThreshold, logger)
        { }

        /// <summary>
        /// Contructor
        /// </summary>
        /// <param name="flowControlThreshold"></param>
        /// <param name="logger"></param>
        public AveragingCachePressureMonitor(double flowControlThreshold, Logger logger)
        {
            this.flowControlThreshold = flowControlThreshold;
            this.logger = logger.GetSubLogger(this.GetType().Name);
            nextCheckedTime = DateTime.MinValue;
            isUnderPressure = false;
        }

        public void RecordCachePressureContribution(double cachePressureContribution)
        {
            // Weight unhealthy contributions thrice as much as healthy ones.
            // This is a crude compensation for the fact that healthy consumers wil consume more often than unhealthy ones.
            double weight = cachePressureContribution < flowControlThreshold ? 1.0 : 3.0;
            accumulatedCachePressure += cachePressureContribution * weight;
            cachePressureContributionCount += weight;
        }

        public bool IsUnderPressure(DateTime utcNow)
        {
            if (nextCheckedTime < utcNow)
            {
                CalculatePressure();
                nextCheckedTime = utcNow + checkPeriod;
            }
            return isUnderPressure;
        }

        private void CalculatePressure()
        {
            // if we don't have any contributions, don't change status
            if (cachePressureContributionCount < 0.5)
            {
                // after 5 checks with no contributions, check anyway
                cachePressureContributionCount += 0.1;
                return;
            }

            double pressure = accumulatedCachePressure / cachePressureContributionCount;
            bool wasUnderPressure = isUnderPressure;
            isUnderPressure = pressure > flowControlThreshold;
            // If we changed state, log
            if (isUnderPressure != wasUnderPressure)
            {
                logger.Verbose(isUnderPressure
                    ? $"Ingesting messages too fast. Throttling message reading. AccumulatedCachePressure: {accumulatedCachePressure}, Contributions: {cachePressureContributionCount}, AverageCachePressure: {pressure}, Threshold: {flowControlThreshold}"
                    : $"Message ingestion is healthy. AccumulatedCachePressure: {accumulatedCachePressure}, Contributions: {cachePressureContributionCount}, AverageCachePressure: {pressure}, Threshold: {flowControlThreshold}");
            }
            cachePressureContributionCount = 0.0;
            accumulatedCachePressure = 0.0;
        }
    }
}
