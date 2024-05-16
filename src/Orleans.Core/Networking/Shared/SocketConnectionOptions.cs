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
        /// The number of I/O queues used to process requests. Set to 0 to directly schedule I/O to the ThreadPool.
        /// </summary>
        /// <remarks>
        /// Defaults to <see cref="Environment.ProcessorCount" /> rounded down and clamped between 1 and 16.
        /// </remarks>
        public int IOQueueCount { get; set; } = Math.Min(Environment.ProcessorCount, 16);

        /// <summary>
        /// Whether the Nagle algorithm should be enabled or disabled.
        /// </summary>
        public bool NoDelay { get; set; } = true;

        /// <summary>
        /// Whether TCP KeepAlive is enabled or disabled.
        /// </summary>
        public bool KeepAlive { get; set; } = true;

        /// <summary>
        /// The number of seconds before the first keep-alive packet is sent on an idle connection.
        /// </summary>
        /// <seealso cref="System.Net.Sockets.SocketOptionName.TcpKeepAliveTime"/>
        public int KeepAliveTimeSeconds { get; set; } = 90;

        /// <summary>
        /// The number of seconds between keep-alive packets when the remote endpoint is not responding.
        /// </summary>
        /// <seealso cref="System.Net.Sockets.SocketOptionName.TcpKeepAliveInterval"/>
        public int KeepAliveIntervalSeconds { get; set; } = 30;

        /// <summary>
        /// The number of retry attempts for keep-alive packets before the connection is considered dead.
        /// </summary>
        /// <seealso cref="System.Net.Sockets.SocketOptionName.TcpKeepAliveRetryCount"/>
        public int KeepAliveRetryCount { get; set; } = 10;

        /// <summary>
        /// Gets or sets the memory pool factory.
        /// </summary>
        internal Func<MemoryPool<byte>> MemoryPoolFactory { get; set; } = () => KestrelMemoryPool.Create();
    }
}
