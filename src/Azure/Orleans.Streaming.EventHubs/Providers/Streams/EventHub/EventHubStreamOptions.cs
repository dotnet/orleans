
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Orleans.Runtime.Configuration;

namespace Orleans.Configuration
{
    /// <summary>
    /// EventHub settings for a specific hub
    /// </summary>
    public class EventHubStreamOptions : RecoverableStreamOptions
    {
        /// <summary>
        /// EventHub connection string.
        /// </summary>
        [Redact]
        public string ConnectionString { get; set; }
        /// <summary>
        /// EventHub consumer group.
        /// </summary>
        public string ConsumerGroup { get; set; }
        /// <summary>
        /// Hub path.
        /// </summary>
        public string Path { get; set; }
        /// <summary>
        /// Optional parameter that configures the receiver prefetch count.
        /// </summary>
        public int? PrefetchCount { get; set; }
        /// <summary>
        /// In cases where no checkpoint is found, this indicates if service should read from the most recent data, or from the begining of a partition.
        /// </summary>
        public bool StartFromNow { get; set; } = DEFAULT_START_FROM_NOW;
        public const bool DEFAULT_START_FROM_NOW = true;

        /// <summary>
        /// SlowConsumingPressureMonitorConfig
        /// </summary>
        public double? SlowConsumingMonitorFlowControlThreshold { get; set; }

        /// <summary>
        /// SlowConsumingMonitorPressureWindowSize
        /// </summary>
        public TimeSpan? SlowConsumingMonitorPressureWindowSize { get; set; }

        /// <summary>
        /// AveragingCachePressureMonitorFlowControlThreshold, AveragingCachePressureMonitor is turn on by default. 
        /// User can turn it off by setting this value to null
        /// </summary>
        public double? AveragingCachePressureMonitorFlowControlThreshold { get; set; } = DEFAULT_AVERAGING_CACHE_PRESSURE_MONITORING_THRESHOLD;
        public const double AVERAGING_CACHE_PRESSURE_MONITORING_OFF = 1.0;
        public const double DEFAULT_AVERAGING_CACHE_PRESSURE_MONITORING_THRESHOLD = 1.0 / 3.0;

        /// <summary>
        /// Azure table storage connections string.
        /// </summary>
        [RedactConnectionString]
        public string CheckpointConnectionString { get; set; }
        /// <summary>
        /// Azure table name.
        /// </summary>
        public string CheckpointTableName { get; set; }
        /// <summary>
        /// Interval to write checkpoints.  Prevents spamming storage.
        /// </summary>
        public TimeSpan CheckpointPersistInterval { get; set; } = DEFAULT_CHECKPOINT_PERSIST_INTERVAL;
        public static readonly TimeSpan DEFAULT_CHECKPOINT_PERSIST_INTERVAL = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Unique namespace for checkpoint data.  Is similar to consumer group.
        /// </summary>
        public string CheckpointNamespace { get; set; }
    }
}