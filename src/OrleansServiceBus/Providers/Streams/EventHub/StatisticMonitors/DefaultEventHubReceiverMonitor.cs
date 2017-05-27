﻿
using System;
using System.Collections.Generic;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Default EventHub receiver monitor that tracks metrics using loggers PKI support.
    /// </summary>
    public class DefaultEventHubReceiverMonitor : IEventHubReceiverMonitor
    {
        private readonly Logger logger;
        private readonly Dictionary<string, string> logProperties;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="dimentions">Aggregation Dimention bag for EventhubReceiverMonitor</param>
        /// <param name="logger"></param>
        public DefaultEventHubReceiverMonitor(EventHubReceiverMonitorDimentions dimentions, Logger logger)
        {
            this.logger = logger;
            logProperties = new Dictionary<string, string>
            {
                {"Path", dimentions.EventHubPath},
                {"Partition", dimentions.EventHubPartition}
            };
        }

        /// <summary>
        /// Track attempts to initialize the receiver.
        /// </summary>
        /// <param name="success">True if read succeeded, false if read failed.</param>
        /// <param name="callTime"></param>
        /// <param name="exception"></param>
        public void TrackInitialization(bool success, TimeSpan callTime, Exception exception)
        {
            logger.TrackMetric("InitializationFailure", success ? 0 : 1, this.logProperties);
            logger.TrackMetric("InitializationCallTime", callTime, this.logProperties);
            logger.TrackMetric("InitializationException", exception == null? 0 : 1, this.logProperties);
        }

        /// <summary>
        /// Track attempts to read from the partition.    Tracked per partition read operation.
        /// </summary>
        /// <param name="success">True if read succeeded, false if read failed.</param>
        /// <param name="callTime"></param>
        /// <param name="exception"></param>
        public void TrackRead(bool success, TimeSpan callTime, Exception exception)
        {
            logger.TrackMetric("ReadFailure", success ? 0 : 1, this.logProperties);
            logger.TrackMetric("ReadCallTime", callTime, this.logProperties);
            logger.TrackMetric("ReadException", exception == null ? 0 : 1, this.logProperties);
        }

        /// <summary>
        /// Tracks messages read and time taken per successful read.  Tracked per successful partition read operation.
        /// </summary>
        /// <param name="count">Messages read.</param>
        /// <param name="oldest"></param>
        /// <param name="newest"></param>
        public void TrackMessagesReceived(long count, TimeSpan? oldest, TimeSpan? newest)
        {
            logger.TrackMetric("MessagesRecieved", count, this.logProperties);
            if(oldest.HasValue)
                logger.TrackMetric("OldestMessageRead", oldest.Value, this.logProperties);
            if(newest.HasValue)
                logger.TrackMetric("NewestMessageRead", newest.Value, this.logProperties);
        }

        /// <summary>
        /// Track attempts to shutdown the receiver.
        /// </summary>
        /// <param name="success">True if read succeeded, false if read failed.</param>
        /// <param name="callTime"></param>
        /// <param name="exception"></param>
        public void TrackShutdown(bool success, TimeSpan callTime, Exception exception)
        {
            logger.TrackMetric("ShutdownFailure", success ? 0 : 1, this.logProperties);
            logger.TrackMetric("ShutdownCallTime", callTime, this.logProperties);
            logger.TrackMetric("ShutdownException", exception == null? 0:1, this.logProperties);
        }
    }

}
