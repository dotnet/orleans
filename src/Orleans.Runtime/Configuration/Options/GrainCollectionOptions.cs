using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime.CollectionGuards;

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
        /// If used with <see cref="ProcessMemoryGrainCollectionGuard"/>, this sets the threshold in bytes for when
        /// the system will start evicting grains.
        ///
        /// If set to 0 or null, the grains will never evade eviction based on GC memory pressure.
        /// </summary>
        public long? CollectionGCMemoryThreshold { get; set; }

        /// <summary>
        /// If used with <see cref="SystemMemoryGrainCollectionGuard"/>, this sets the threshold in bytes for when
        /// how much memory must be available for the system to start evicting grains.
        ///
        /// If set to 0 or null, the grains will never evade eviction based on available system memory.
        /// </summary>
        public long? CollectionSystemMemoryFreeThreshold { get; set; }

        /// <summary>
        /// If used with <see cref="SystemMemoryGrainCollectionGuard"/>, this sets the threshold in percent for when
        /// how much memory must be available for the system to start evicting grains.
        ///
        /// If set to 0 or null, the grains will never evade eviction based on available system memory.
        ///
        /// The range is from 0.0 to 100.0.
        /// </summary>
        public float? CollectionSystemMemoryFreePercentThreshold { get; set; }

        /// <summary>
        /// When collection starts, how often should we check if we should continue collection. This
        /// value is only user if collection guards are enabled.
        ///
        /// 0 means that guards will not be checked during collection. This is probably not ideal
        /// if guards are in place because "a lot" of grains will be deactivated at once.
        /// </summary>
        public int CollectionBatchSize { get; set; } = 0;

        /// <summary>
        /// When finished collecting a batch, optionally wait for a period of time before starting the next batch.
        /// Typically this is used to allow the system to perform GC.
        ///
        /// This value will be used with a call to Task.Delay.
        ///
        /// If it is 0, no call to Task.Delay will be made.
        /// </summary>
        public int CollectionBatchDelay { get; set; } = 0;

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
    }
}
