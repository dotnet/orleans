﻿using Orleans.Runtime;
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
        /// <summary>
        /// DefaultPressureWindowSize
        /// </summary>
        public static TimeSpan DefaultPressureWindowSize = TimeSpan.FromMinutes(1);
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
        private double biggestPressureInCurrentWindow;
        private DateTime nextCheckedTime;
        private bool wasUnderPressure;

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
            this.biggestPressureInCurrentWindow = 0;
            this.wasUnderPressure = false;
            this.PressureWindowSize = pressureWindowSzie;
        }

        /// <inheritdoc />
        public void RecordCachePressureContribution(double cachePressureContribution)
        {
            if (cachePressureContribution > this.biggestPressureInCurrentWindow)
                biggestPressureInCurrentWindow = cachePressureContribution;
        }

        /// <inheritdoc />
        public bool IsUnderPressure(DateTime utcNow)
        {
            //if any pressure contribution in current period is bigger than flowControlThreshold
            //we see the cache is under pressure
            bool underPressure = this.biggestPressureInCurrentWindow > this.FlowControlThreshold;

            if (underPressure && !this.wasUnderPressure)
            {
                //if under pressure, extend the nextCheckedTime, make sure wasUnderPressure is true for a whole window  
                this.wasUnderPressure = underPressure;
                this.nextCheckedTime = utcNow + this.PressureWindowSize;
                logger.Info($"Ingesting messages too fast. Throttling message reading. BiggestPressureInCurrentPeriod: {biggestPressureInCurrentWindow}, Threshold: {FlowControlThreshold}");
                this.biggestPressureInCurrentWindow = 0;
            }

            if (this.nextCheckedTime < utcNow)
            {
                //at the end of each check period, reset biggestPressureInCurrentPeriod
                this.nextCheckedTime = utcNow + this.PressureWindowSize;
                this.biggestPressureInCurrentWindow = 0;
                //if at the end of the window, pressure clears out, log
                if(this.wasUnderPressure && !underPressure)
                    logger.Info($"Message ingestion is healthy. BiggestPressureInCurrentPeriod: {biggestPressureInCurrentWindow}, Threshold: {FlowControlThreshold}");
                this.wasUnderPressure = underPressure;
            }

            return this.wasUnderPressure;
        }
    }
}
