using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for configuring scheduler behavior.
    /// </summary>
    public class SchedulingOptions
    {
        /// <summary>
        /// Gets or sets the work item queuing delay threshold, at which a warning log message is written.
        /// That is, if the delay between enqueuing the work item and executing the work item is greater than DelayWarningThreshold, a warning log is written.
        /// </summary>
        public TimeSpan DelayWarningThreshold { get; set; } = DEFAULT_DELAY_WARNING_THRESHOLD;

        /// <summary>
        /// The default value for <see cref="DelayWarningThreshold"/>.
        /// </summary>
        public static readonly TimeSpan DEFAULT_DELAY_WARNING_THRESHOLD = TimeSpan.FromMilliseconds(10000); // 10 seconds

        /// <summary>
        /// Gets or sets the soft time limit on the duration of activation macro-turn (a number of micro-turns). 
        /// If an activation was running its micro-turns longer than this, we will give up the thread.
        /// If this is set to zero or a negative number, then the full work queue is drained (MaxWorkItemsPerTurn allowing).
        /// </summary>
        public TimeSpan ActivationSchedulingQuantum { get; set; } = DEFAULT_ACTIVATION_SCHEDULING_QUANTUM;

        /// <summary>
        /// The default value for <see cref="ActivationSchedulingQuantum"/>.
        /// </summary>
        public static readonly TimeSpan DEFAULT_ACTIVATION_SCHEDULING_QUANTUM = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// Gets or sets the soft time limit to generate trace warning when the micro-turn executes longer then this period in CPU. 
        /// </summary>
        public TimeSpan TurnWarningLengthThreshold { get; set; } = DEFAULT_TURN_WARNING_THRESHOLD;

        /// <summary>
        /// The default value for <see cref="TurnWarningLengthThreshold"/>.
        /// </summary>
        public static readonly TimeSpan DEFAULT_TURN_WARNING_THRESHOLD = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// Gets or sets the per work group limit of how many items can be queued up before warnings are generated.
        /// </summary>
        public int MaxPendingWorkItemsSoftLimit { get; set; } = DEFAULT_MAX_PENDING_ITEMS_SOFT_LIMIT;

        /// <summary>
        /// The default value for <see cref="MaxPendingWorkItemsSoftLimit"/>.
        /// </summary>
        public const int DEFAULT_MAX_PENDING_ITEMS_SOFT_LIMIT = 0;

        /// <summary>
        /// Gets or sets the period of time after which to log errors for tasks scheduled to stopped activations.
        /// </summary>
        public TimeSpan StoppedActivationWarningInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Whether or not to allow reentrancy for calls within the same call chain.
        /// </summary>
        public bool AllowCallChainReentrancy { get; set; } = DEFAULT_ALLOW_CALL_CHAIN_REENTRANCY;
        public const bool DEFAULT_ALLOW_CALL_CHAIN_REENTRANCY = true;
    }
}