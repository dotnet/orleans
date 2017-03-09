using System;


namespace Orleans.Runtime
{
    /// <summary>
    /// Snapshot of current runtime statistics for a silo
    /// </summary>
    [Serializable]
    public class SiloRuntimeStatistics
    {
        /// <summary>
        /// Total number of activations in a silo.
        /// </summary>
        public int ActivationCount { get; internal set; }

        /// <summary>
        /// Number of activations in a silo that have been recently used.
        /// </summary>
        public int RecentlyUsedActivationCount { get; internal set; }

        /// <summary>
        /// The size of the request queue.
        /// </summary>
        public long RequestQueueLength { get; internal set; }

        /// <summary>
        /// The size of the sending queue.
        /// </summary>
        public int SendQueueLength { get; internal set; }

        /// <summary>
        /// The size of the receiving queue.
        /// </summary>
        public int ReceiveQueueLength { get; internal set; }

        /// <summary>
        /// The CPU utilization.
        /// </summary>
        public float CpuUsage { get; internal set; }

        /// <summary>
        /// The amount of memory available in the silo [bytes].
        /// </summary>
        public float AvailableMemory { get; internal set; }

        /// <summary>
        /// The used memory size.
        /// </summary>
        public long MemoryUsage { get; internal set; }

        /// <summary>
        /// The total physical memory available [bytes].
        /// </summary>
        public long TotalPhysicalMemory { get; internal set; }

        /// <summary>
        /// Is this silo overloaded.
        /// </summary>
        public bool IsOverloaded { get; internal set; }

        /// <summary>
        /// The number of clients currently connected to that silo.
        /// </summary>
        public long ClientCount { get; internal set; }

        public long ReceivedMessages { get; internal set; }

        public long SentMessages { get; internal set; }


        /// <summary>
        /// The DateTime when this statistics was created.
        /// </summary>
        public DateTime DateTime { get; private set; }

        internal SiloRuntimeStatistics() { }

        internal SiloRuntimeStatistics(ISiloPerformanceMetrics metrics, DateTime dateTime)
        {
            ActivationCount = metrics.ActivationCount;
            RecentlyUsedActivationCount = metrics.RecentlyUsedActivationCount;
            RequestQueueLength = metrics.RequestQueueLength;
            SendQueueLength = metrics.SendQueueLength;
            ReceiveQueueLength = metrics.ReceiveQueueLength;
            CpuUsage = metrics.CpuUsage;
            AvailableMemory = metrics.AvailablePhysicalMemory;
            MemoryUsage = metrics.MemoryUsage;
            IsOverloaded = metrics.IsOverloaded;
            ClientCount = metrics.ClientCount;
            TotalPhysicalMemory = metrics.TotalPhysicalMemory;
            ReceivedMessages = metrics.ReceivedMessages;
            SentMessages = metrics.SentMessages;
            DateTime = dateTime;
        }

        public override string ToString()
        {
            return String.Format("SiloRuntimeStatistics: ActivationCount={0} RecentlyUsedActivationCount={11} RequestQueueLength={1} SendQueueLength={2} " +
                                 "ReceiveQueueLength={3} CpuUsage={4} AvailableMemory={5} MemoryUsage={6} IsOverloaded={7} " +
                                 "ClientCount={8} TotalPhysicalMemory={9} DateTime={10}", ActivationCount, RequestQueueLength,
                                 SendQueueLength, ReceiveQueueLength, CpuUsage, AvailableMemory, MemoryUsage, IsOverloaded,
                                 ClientCount, TotalPhysicalMemory, DateTime, RecentlyUsedActivationCount);
        }
    }
}
