﻿
using System;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Responsible for monitoring receiver performance metrics.
    /// </summary>
    public interface IQueueAdapterReceiverMonitor
    {
        #region event driven metrics
        /// <summary>
        /// Track attempts to initialize the receiver.
        /// </summary>
        /// <param name="success">True if read succeeded, false if read failed.</param>
        /// <param name="callTime">Init operation time</param>
        /// <param name="exception">Exception caught if initialize fail</param>
        void TrackInitialization(bool success, TimeSpan callTime, Exception exception);

        /// <summary>
        /// Track attempts to read from the partition.    Tracked per partition read operation.
        /// </summary>
        /// <param name="success">True if read succeeded, false if read failed.</param>
        /// <param name="callTime">Time spent in read operation.</param>
        /// <param name="exception">The exception caught if read failed.</param>
        void TrackRead(bool success, TimeSpan callTime, Exception exception);
        /// <summary>
        /// Tracks messages read and time taken per successful read.  Tracked per successful partition read operation.
        /// </summary>
        /// <param name="count">Messages read.</param>
        /// <param name="oldestMessageEnqueueTimeUtc"></param>
        /// <param name="newestMessageEnqueueTimeUtc"></param>
        void TrackMessagesReceived(long count, DateTime? oldestMessageEnqueueTimeUtc, DateTime? newestMessageEnqueueTimeUtc);

        /// <summary>
        /// Track attempts to shutdown the receiver.
        /// </summary>
        /// <param name="success">True if read succeeded, false if read failed.</param>
        /// <param name="callTime">Shutdown operation time</param>
        /// <param name="exception">Exception caught if shutdown fail</param>
        void TrackShutdown(bool success, TimeSpan callTime, Exception exception);
        #endregion
    }
}
