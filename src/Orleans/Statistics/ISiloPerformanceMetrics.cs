namespace Orleans.Runtime
{
    /// <summary>
    /// A small set of per-silo important key performance metrics
    /// </summary>
    public interface ISiloPerformanceMetrics : ICorePerformanceMetrics
    {   
        /// <summary>
        /// the current size of the receive queue (number of messages that arrived to this silo and 
        /// are waiting to be dispatched). Captures both remote and local messages from other silos 
        /// as well as from the clients.
        /// </summary>
        long RequestQueueLength { get; }

        /// <summary>
        /// number of activations on this silo
        /// </summary>
        int ActivationCount { get; }

        /// <summary>
        /// Number of activations on this silo that were used in the last 10 minutes 
        /// (Note: this number may currently not be accurate if different age limits 
        /// are used for different grain types).
        /// </summary>
        int RecentlyUsedActivationCount { get; }

        /// <summary>
        /// Number of currently connected clients
        /// </summary>
        long ClientCount { get; }

        /// <summary>
        /// whether this silo is currently overloaded and is in the load shedding mode.
        /// </summary>
        bool IsOverloaded { get; } 

        void LatchIsOverload(bool overloaded); // For testing only
        void UnlatchIsOverloaded(); // For testing only
        void LatchCpuUsage(float value); // For testing only
        void UnlatchCpuUsage(); // For testing only
    }
}