namespace Orleans.Runtime.Scheduler
{
    /// <summary>
    /// Options for configuring scheduler behavior.
    /// </summary>
    public class SchedulingOptions
    {
        /// <summary>
        /// Whether or not to perform deadlock detection.
        /// </summary>
        public bool PerformDeadlockDetection { get; set; }

        /// <summary>
        /// Whether or not to allow reentrancy for calls within the same call chain.
        /// </summary>
        public bool AllowCallChainReentrancy { get; set; }
    }
}