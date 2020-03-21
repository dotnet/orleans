using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configures networking options.
    /// </summary>
    [Obsolete("Use " + nameof(ConnectionOptions))]
    public class NetworkingOptions
    {
        /// <summary>
        /// The OpenConnectionTimeout attribute specifies the timeout before a connection open is assumed to have failed
        /// </summary>
        [Obsolete("Use " + nameof(ConnectionOptions) + "." + nameof(ConnectionOptions.OpenConnectionTimeout))]
        public TimeSpan OpenConnectionTimeout { get; set; } = DEFAULT_OPENCONNECTION_TIMEOUT;
        public static readonly TimeSpan DEFAULT_OPENCONNECTION_TIMEOUT = TimeSpan.FromSeconds(5);

        /// <summary>
        /// The MaxSocketAge attribute specifies how long to keep an open socket before it is closed.
        /// Default is TimeSpan.MaxValue (never close sockets automatically, unless they were broken).
        /// </summary>
        [Obsolete("Not supported")]
        public TimeSpan MaxSocketAge { get; set; } = DEFAULT_MAX_SOCKET_AGE;
        public static readonly TimeSpan DEFAULT_MAX_SOCKET_AGE = TimeSpan.MaxValue;

        /// <summary>
        /// The MaxSockets attribute defines the maximum number of TCP sockets a silo would keep open at any point in time.
        /// When the limit is reached, least recently used sockets will be closed to keep the number of open sockets below the limit.
        /// </summary>
        [Obsolete("Not supported")]
        public int MaxSockets { get; set; } = DEFAULT_MAX_SOCKETS;
        public static readonly int DEFAULT_MAX_SOCKETS = 500;
    }
}
