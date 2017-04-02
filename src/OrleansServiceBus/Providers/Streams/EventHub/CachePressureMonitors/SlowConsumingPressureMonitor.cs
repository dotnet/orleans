using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Pressure monitor which is in favor of the slow consumer in the cache
    /// </summary>
    public class SlowConsumingPressureMonitor : ICachePressureMonitor
    {
        private static TimeSpan DefaultPressureWindowSize = TimeSpan.FromMinutes(1);
        private const double DefaultFlowControlThreshold = 0.5;

        /// <summary>
        /// PressureWindowSize
        /// </summary>
        public TimeSpan PressureWindowSize { get; set; }
        /// <summary>
        /// FlowControlThreshold
        /// </summary>
        public double FlowControlThreshold { get; set; }

        private readonly Logger logger;
        private double biggestPressureInCurrentPeriod;
        private DateTime nextCheckedTime;
        private bool isUnderPressure;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="logger"></param>
        public SlowConsumingPressureMonitor(Logger logger)
            : this(DefaultFlowControlThreshold, DefaultPressureWindowSize, logger)
        { }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="pressureWindowSize"></param>
        /// <param name="logger"></param>
        public SlowConsumingPressureMonitor(TimeSpan pressureWindowSize, Logger logger)
            : this(DefaultFlowControlThreshold, pressureWindowSize, logger)
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="flowControlThreshold"></param>
        /// <param name="logger"></param>
        public SlowConsumingPressureMonitor(double flowControlThreshold, Logger logger)
            : this(flowControlThreshold, DefaultPressureWindowSize, logger)
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="flowControlThreshold"></param>
        /// <param name="pressureWindowSzie"></param>
        /// <param name="logger"></param>
        public SlowConsumingPressureMonitor(double flowControlThreshold, TimeSpan pressureWindowSzie, Logger logger)
        {
            this.FlowControlThreshold = flowControlThreshold;
            this.logger = logger.GetSubLogger(this.GetType().Name);
            this.nextCheckedTime = DateTime.MinValue;
            this.biggestPressureInCurrentPeriod = 0;
            this.isUnderPressure = false;
            this.PressureWindowSize = pressureWindowSzie;
        }

        public void RecordCachePressureContribution(double cachePressureContribution)
        {
            if (cachePressureContribution > biggestPressureInCurrentPeriod)
                biggestPressureInCurrentPeriod = cachePressureContribution;
        }

        public bool IsUnderPressure(DateTime utcNow)
        {
            //if any pressure contribution in current period is bigger than flowControlThreshold
            //we see the cache is under pressure
            bool underPressure = this.biggestPressureInCurrentPeriod > this.FlowControlThreshold;
            if (this.isUnderPressure != underPressure)
            {
                this.isUnderPressure = underPressure;
                logger.Info(this.isUnderPressure
                    ? $"Ingesting messages too fast. Throttling message reading. BiggestPressureInCurrentPeriod: {biggestPressureInCurrentPeriod}, Threshold: {FlowControlThreshold}"
                    : $"Message ingestion is healthy. BiggestPressureInCurrentPeriod: {biggestPressureInCurrentPeriod}, Threshold: {FlowControlThreshold}");
            }

            if (nextCheckedTime < utcNow)
            {
                //at the end of each check period, reset biggestPressureInCurrentPeriod
                this.nextCheckedTime = utcNow + this.PressureWindowSize;
                this.biggestPressureInCurrentPeriod = 0;
            }
            return underPressure;
        }
    }
}
