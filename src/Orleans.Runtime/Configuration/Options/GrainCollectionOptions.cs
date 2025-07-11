using System;
using System.Collections.Generic;

namespace Orleans.Configuration
{
    /// <summary>
    /// Silo options for idle grain collection.
    /// </summary>
    public class GrainCollectionOptions
    {
        /// <summary>
        /// Regulates the periodic collection of inactive grains.
        /// </summary>
        public TimeSpan CollectionQuantum { get; set; } = DEFAULT_COLLECTION_QUANTUM;

        /// <summary>
        /// The default value for <see cref="CollectionQuantum"/>.
        /// </summary>
        public static readonly TimeSpan DEFAULT_COLLECTION_QUANTUM = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Gets or sets the default period of inactivity necessary for a grain to be available for collection and deactivation.
        /// </summary>
        public TimeSpan CollectionAge { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>
        /// Period of inactivity necessary for a grain to be available for collection and deactivation by grain type.
        /// </summary>
        public Dictionary<string, TimeSpan> ClassSpecificCollectionAge { get; set; } = new Dictionary<string, TimeSpan>();

        /// <summary>
        /// Timeout value before giving up when trying to activate a grain.
        /// </summary>
        public TimeSpan ActivationTimeout { get; set; } = DEFAULT_ACTIVATION_TIMEOUT;

        /// <summary>
        /// The default value for <see cref="ActivationTimeout"/>.
        /// </summary>
        public static readonly TimeSpan DEFAULT_ACTIVATION_TIMEOUT = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Timeout value before giving up when trying to deactivate a grain activation
        /// (waiting for all timers to stop and calling Grain.OnDeactivate())
        /// </summary>
        public TimeSpan DeactivationTimeout { get; set; } = DEFAULT_DEACTIVATION_TIMEOUT;

        /// <summary>
        /// The default value for <see cref="DeactivationTimeout"/>.
        /// </summary>
        public static readonly TimeSpan DEFAULT_DEACTIVATION_TIMEOUT = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Indicates if memory pressure should trigger grain activation shedding.
        /// </summary>
        /// <remarks>
        /// If set to true, the silo will shed grain activations when memory usage exceeds the specified limits.
        /// See <see cref="MemoryUsageLimitPercentage"/>, <see cref="MemoryUsageTargetPercentage"/>, and <see cref="MemoryUsagePollingPeriod"/> for configuration.
        /// </remarks>
        public bool EnableActivationSheddingOnMemoryPressure { get; set; }

        /// <summary>
        /// The interval at which memory usage is polled.
        /// </summary>
        public TimeSpan MemoryUsagePollingPeriod { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// The memory usage percentage (0–100) at which grain collection is triggered.
        /// Must be greater than 0 and less than or equal to 100.
        /// </summary>
        public double MemoryUsageLimitPercentage { get; set; } = 80;

        /// <summary>
        /// The target memory usage percentage (0–100) to reach after grain collection.
        /// Must be greater than 0, less than or equal to 100, and less than <see cref="MemoryUsageLimitPercentage"/>.
        /// </summary>
        public double MemoryUsageTargetPercentage { get; set; } = 75;
    }
}
