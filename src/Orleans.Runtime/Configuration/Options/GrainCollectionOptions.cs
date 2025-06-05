using System;
using System.Collections.Generic;

namespace Orleans.Configuration
{
    /// <summary>
    /// Silo options for grain garbage collection.
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
        /// Controls behavior of grain collection based on available memory.
        /// Must be assigned to a non-null value to be enabled.
        /// </summary>
        public MemoryBasedGrainCollectionOptions MemoryBasedOptions { get; set; } = null!;
    }

    public class MemoryBasedGrainCollectionOptions
    {
        /// <summary>
        /// Regulates the periodic check of memory load.
        /// </summary>
        public TimeSpan MemoryLoadValidationQuantum { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Memory load percentage threshold above which grain collection will be triggered.
        /// </summary>
        public double MemoryLoadThresholdPercentage { get; set; } = 90;

        /// <summary>
        /// Controls how many of the grains should be collected when the <see cref="MemoryLoadThresholdPercentage"/> is exceeded.
        /// Targets the memory load percentage which node will be running at after the grain collection happened.
        /// </summary>
        public double TargetMemoryLoadPercentage { get; set; } = 80;

        /// <summary>
        /// Heap memory threshold in megabytes above which grain collection will be triggered.
        /// </summary>
        public double HeapMemoryThresholdMb { get; set; } = 0;

        /// <summary>
        /// Target heap memory in megabytes after grain collection.
        /// </summary>
        public double TargetHeapMemoryMb { get; set; } = 0;

        /// <summary>
        /// Determines which memory threshold mode to use.
        /// </summary>
        public MemoryThresholdMode ThresholdMode { get; set; } = MemoryThresholdMode.Relative;
    }

    /// <summary>
    /// Specifies the mode for memory threshold evaluation.
    /// </summary>
    public enum MemoryThresholdMode
    {
        Relative,
        AbsoluteMb
    }
}
