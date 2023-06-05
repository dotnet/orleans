using Orleans.Providers.Streams.Common;
using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Streaming.EventHubs
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
            FlowControlThreshold = flowControlThreshold;
            this.logger = logger;
            nextCheckedTime = DateTime.MinValue;
            biggestPressureInCurrentWindow = 0;
            wasUnderPressure = false;
            CacheMonitor = monitor;
            PressureWindowSize = pressureWindowSzie;
        }

        /// <inheritdoc />
        public void RecordCachePressureContribution(double cachePressureContribution)
        {
            if (cachePressureContribution > biggestPressureInCurrentWindow)
                biggestPressureInCurrentWindow = cachePressureContribution;
        }

        /// <inheritdoc />
        public bool IsUnderPressure(DateTime utcNow)
        {
            //if any pressure contribution in current period is bigger than flowControlThreshold
            //we see the cache is under pressure
            bool underPressure = biggestPressureInCurrentWindow > FlowControlThreshold;

            if (underPressure && !wasUnderPressure)
            {
                //if under pressure, extend the nextCheckedTime, make sure wasUnderPressure is true for a whole window  
                wasUnderPressure = underPressure;
                nextCheckedTime = utcNow + PressureWindowSize;
                CacheMonitor?.TrackCachePressureMonitorStatusChange(GetType().Name, underPressure, null, biggestPressureInCurrentWindow, FlowControlThreshold);
                if(logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug(
                        "Ingesting messages too fast. Throttling message reading. BiggestPressureInCurrentPeriod: {BiggestPressureInCurrentWindow}, Threshold: {FlowControlThreshold}",
                        biggestPressureInCurrentWindow,
                        FlowControlThreshold);
                biggestPressureInCurrentWindow = 0;
            }

            if (nextCheckedTime < utcNow)
            {
                //at the end of each check period, reset biggestPressureInCurrentPeriod
                nextCheckedTime = utcNow + PressureWindowSize;
                biggestPressureInCurrentWindow = 0;
                //if at the end of the window, pressure clears out, log
                if (wasUnderPressure && !underPressure)
                {
                    CacheMonitor?.TrackCachePressureMonitorStatusChange(GetType().Name, underPressure, null, biggestPressureInCurrentWindow, FlowControlThreshold);
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.LogDebug(
                            "Message ingestion is healthy. BiggestPressureInCurrentPeriod: {BiggestPressureInCurrentWindow}, Threshold: {FlowControlThreshold}",
                            biggestPressureInCurrentWindow,
                            FlowControlThreshold);
                }
                wasUnderPressure = underPressure;
            }

            return wasUnderPressure;
        }
    }
}
