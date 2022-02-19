using System;
using System.Buffers;

namespace Orleans.Networking.Shared
{
    /// <summary>
    /// Options for configuring socket connections.
    /// </summary>
    public class SocketConnectionOptions
    {
        /// <summary>
        /// Gets or sets the number of I/O queues used to process requests. Set to 0 to directly schedule I/O to the ThreadPool.
        /// </summary>
        /// <remarks>
        /// Defaults to <see cref="Environment.ProcessorCount" /> rounded down and clamped between 1 and 16.
        /// </remarks>
        public int IOQueueCount { get; set; } = Math.Min(Environment.ProcessorCount, 16);

        /// <summary>
        /// Gets or sets a value indicating whether the Nagle algorithm should be enabled or disabled.
        /// </summary>
        public bool NoDelay { get; set; } = true;

        /// <summary>
        /// Gets or sets the memory pool factory.
        /// </summary>
        internal Func<MemoryPool<byte>> MemoryPoolFactory { get; set; } = () => KestrelMemoryPool.Create();
    }
}
