using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for configuring scheduler behavior.
    /// </summary>
    public class SchedulingOptions
    {
        /// <summary>
        /// Configuration type that controls the type of the grain directory caching algorithm that silo use.
        /// </summary>
        public enum CallChainReentrancyStrategy
        {
            /// <summary>Don't allow reentrancy.</summary>
            None,
            /// <summary>Allow reentrancy for a single call (A->B->A).</summary>
            SingleCall,
            /// <summary>Allow reentrancy for for entire call chain (A->B->C->A).</summary>
            EntireChain
        }

        /// <summary>
        /// Whether or not to perform deadlock detection.
        /// </summary>
        public bool PerformDeadlockDetection { get; set; } = DEFAULT_PERFORM_DEADLOCK_DETECTION;
        public const bool DEFAULT_PERFORM_DEADLOCK_DETECTION = false;

        /// <summary>
        /// Whether or not to allow reentrancy for calls within the same call chain.
        /// </summary>
        [Obsolete("AllowCallChainReentrancy is obsolete, use CallChainReentrancy. None is equivalent to false, SingleCall is equivalent to true")]
        public bool AllowCallChainReentrancy {
            get => CallChainReentrancy != CallChainReentrancyStrategy.None;
            set => CallChainReentrancy = value ? CallChainReentrancyStrategy.SingleCall : CallChainReentrancyStrategy.None;
        }

        /// <summary>
        /// How to handle reentrancy for calls within the same call chain.
        /// </summary>
        public CallChainReentrancyStrategy CallChainReentrancy { get; set; } = DEFAULT_CALL_CHAIN_REENTRANCY;
        public const CallChainReentrancyStrategy DEFAULT_CALL_CHAIN_REENTRANCY = CallChainReentrancyStrategy.SingleCall;

        /// <summary>
        /// The MaxActiveThreads attribute specifies the maximum number of simultaneous active threads the scheduler will allow.
        /// Generally this number should be roughly equal to the number of cores on the node.
        /// </summary>
        public int MaxActiveThreads { get; set; } = DEFAULT_MAX_ACTIVE_THREADS;
        public static readonly int DEFAULT_MAX_ACTIVE_THREADS = Math.Max(4, System.Environment.ProcessorCount);

        /// <summary>
        /// The DelayWarningThreshold attribute specifies the work item queuing delay threshold, at which a warning log message is written.
        /// That is, if the delay between enqueuing the work item and executing the work item is greater than DelayWarningThreshold, a warning log is written.
        /// </summary>
        public TimeSpan DelayWarningThreshold { get; set; } = DEFAULT_DELAY_WARNING_THRESHOLD;
        public static readonly TimeSpan DEFAULT_DELAY_WARNING_THRESHOLD = TimeSpan.FromMilliseconds(10000); // 10 seconds

        /// <summary>
        /// ActivationSchedulingQuantum is a soft time limit on the duration of activation macro-turn (a number of micro-turns).
        /// If an activation was running its micro-turns longer than this, we will give up the thread.
        /// If this is set to zero or a negative number, then the full work queue is drained (MaxWorkItemsPerTurn allowing).
        /// </summary>
        public TimeSpan ActivationSchedulingQuantum { get; set; } = DEFAULT_ACTIVATION_SCHEDULING_QUANTUM;
        public static readonly TimeSpan DEFAULT_ACTIVATION_SCHEDULING_QUANTUM = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// TurnWarningLengthThreshold is a soft time limit to generate trace warning when the micro-turn executes longer then this period in CPU.
        /// </summary>
        public TimeSpan TurnWarningLengthThreshold { get; set; } = DEFAULT_TURN_WARNING_THRESHOLD;
        public static readonly TimeSpan DEFAULT_TURN_WARNING_THRESHOLD = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// Per work group limit of how many items can be queued up before warnings are generated.
        /// </summary>
        public int MaxPendingWorkItemsSoftLimit { get; set; } = DEFAULT_MAX_PENDING_ITEMS_SOFT_LIMIT;
        public const int DEFAULT_MAX_PENDING_ITEMS_SOFT_LIMIT = 0;

        /// <summary>
        /// For test use only.  Do not alter from default in production services
        /// </summary>
        public bool EnableWorkerThreadInjection { get; set; } = DEFAULT_ENABLE_WORKER_THREAD_INJECTION;
        public const bool DEFAULT_ENABLE_WORKER_THREAD_INJECTION = false;
    }
}