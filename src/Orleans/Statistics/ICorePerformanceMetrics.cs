namespace Orleans.Runtime
{
    public interface ICorePerformanceMetrics
    {
        /// <summary>
        /// CPU utilization
        /// </summary>
        float CpuUsage { get; }

        /// <summary>
        /// Amount of memory available to processes running on the machine
        /// </summary>
        long AvailablePhysicalMemory { get; }

        /// <summary>
        /// Current memory usage
        /// </summary>
        long MemoryUsage { get; }

        /// <summary>
        /// Amount of physical memory on the machine
        /// </summary>
        long TotalPhysicalMemory { get; }

        /// <summary>
        /// the current size of the send queue (number of messages waiting to be sent). 
        /// Only captures remote messages to other silos (not including messages to the clients).
        /// </summary>
        int SendQueueLength { get; }

        /// <summary>
        /// the current size of the receive queue (number of messages that arrived to this silo and 
        /// are waiting to be dispatched). Captures both remote and local messages from other silos 
        /// as well as from the clients.
        /// </summary>
        int ReceiveQueueLength { get; }

        /// <summary>
        /// total number of remote messages sent to other silos as well as to the clients.
        /// </summary>
        long SentMessages { get; }

        /// <summary>
        /// total number of remote received messages, from other silos as well as from the clients.
        /// </summary>
        long ReceivedMessages { get; }
    }
}