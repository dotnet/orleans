using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using System;
using Microsoft.Extensions.Logging;

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
        /// <summary>
        /// Cache monitor which is used to report cache related metrics
        /// </summary>
        public ICacheMonitor CacheMonitor { set; private get; }
        /// <summary>
        /// Default flow control threshold
        /// </summary>
        public const double DefaultFlowControlThreshold = 0.5;

        /// <summary>
        /// PressureWindowSize
        /// </summary>
        public TimeSpan PressureWindowSize { get; set; }
        /// <summary>
        /// FlowControlThreshold
        /// </summary>
        public double FlowControlThreshold { get; set; }

        private readonly ILogger logger;
        private double biggestPressureInCurrentWindow;
        private DateTime nextCheckedTime;
        private bool wasUnderPressure;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="monitor"></param>
        public SlowConsumingPressureMonitor(ILogger logger, ICacheMonitor monitor = null)
            : this(DefaultFlowControlThreshold, DefaultPressureWindowSize, logger, monitor)
        { }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="pressureWindowSize"></param>
        /// <param name="logger"></param>
        /// <param name="monitor"></param>
        public SlowConsumingPressureMonitor(TimeSpan pressureWindowSize, ILogger logger, ICacheMonitor monitor = null)
            : this(DefaultFlowControlThreshold, pressureWindowSize, logger, monitor)
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="flowControlThreshold"></param>
        /// <param name="logger"></param>
        /// <param name="monitor"></param>
        public SlowConsumingPressureMonitor(double flowControlThreshold, ILogger logger, ICacheMonitor monitor = null)
            : this(flowControlThreshold, DefaultPressureWindowSize, logger, monitor)
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="flowControlThreshold"></param>
        /// <param name="pressureWindowSzie"></param>
        /// <param name="logger"></param>
        /// <param name="monitor"></param>
        public SlowConsumingPressureMonitor(double flowControlThreshold, TimeSpan pressureWindowSzie, ILogger logger, ICacheMonitor monitor = null)
        {
            this.FlowControlThreshold = flowControlThreshold;
            this.logger = logger;
            this.nextCheckedTime = DateTime.MinValue;
            this.biggestPressureInCurrentWindow = 0;
            this.wasUnderPressure = false;
            this.CacheMonitor = monitor;
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
                this.CacheMonitor?.TrackCachePressureMonitorStatusChange(this.GetType().Name, underPressure, null, biggestPressureInCurrentWindow, this.FlowControlThreshold);
                if(logger.IsEnabled(LogLevel.Debug))
                    logger.Debug($"Ingesting messages too fast. Throttling message reading. BiggestPressureInCurrentPeriod: {biggestPressureInCurrentWindow}, Threshold: {FlowControlThreshold}");
                this.biggestPressureInCurrentWindow = 0;
            }

            if (this.nextCheckedTime < utcNow)
            {
                //at the end of each check period, reset biggestPressureInCurrentPeriod
                this.nextCheckedTime = utcNow + this.PressureWindowSize;
                this.biggestPressureInCurrentWindow = 0;
                //if at the end of the window, pressure clears out, log
                if (this.wasUnderPressure && !underPressure)
                {
                    this.CacheMonitor?.TrackCachePressureMonitorStatusChange(this.GetType().Name, underPressure, null, biggestPressureInCurrentWindow, this.FlowControlThreshold);
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.Debug($"Message ingestion is healthy. BiggestPressureInCurrentPeriod: {biggestPressureInCurrentWindow}, Threshold: {FlowControlThreshold}");
                }
                this.wasUnderPressure = underPressure;
            }

            return this.wasUnderPressure;
        }
    }
}
