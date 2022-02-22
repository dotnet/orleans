using System;
using System.Diagnostics;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    /// <summary>
    /// Specifies global messaging options that are common to client and silo.
    /// </summary>
    public abstract class MessagingOptions
    {
        /// <summary>
        /// The <see cref="ResponseTimeout"/> value.
        /// </summary>
        private TimeSpan _responseTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the default timeout before a request is assumed to have failed.
        /// </summary>
        /// <seealso cref="ResponseTimeoutWithDebugger"/>
        /// <value>Requests will timeout after 30 seconds by default.</value>
        public TimeSpan ResponseTimeout
        {
            get { return Debugger.IsAttached ? ResponseTimeoutWithDebugger : _responseTimeout; }
            set { this._responseTimeout = value; }
        }

        /// <summary>
        /// Gets or sets the effective <see cref="ResponseTimeout"/> value to use when a debugger is attached.
        /// </summary>
        /// <value>Requests will timeout after 30 minutes when a debugger is attached, by default.</value>
        public TimeSpan ResponseTimeoutWithDebugger { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Gets or sets a value indicating whether messages should be dropped once they expire, that is if it was not delivered 
        /// to the destination before it has timed out on the sender.
        /// </summary>
        /// <value>Messages are dropped once they expire, by default.</value>
        public bool DropExpiredMessages { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether the current activity id should be propagated into grain calls.
        /// </summary>
        /// <value>Activity ids are not propagates into grain calls by default.</value>
        public bool PropagateActivityId { get; set; } = false;

        /// <summary>
        /// The maximum number of times a message send attempt will be retried.
        /// </summary>
        internal const int DEFAULT_MAX_MESSAGE_SEND_RETRIES = 1;

        /// <summary>
        /// The maximum size, in bytes, of the header for a message. The runtime will forcibly close the connection
        /// if the header size is greater than this value.
        /// </summary>
        /// <value>The maximum message header size is 10 MB by default.</value>
        public int MaxMessageHeaderSize { get; set; } = 10 * 1024 * 1024;

        /// <summary>
        /// The maximum size, in bytes, of the body for a message. The runtime will forcibly close the connection
        /// if the body size is greater than this value.
        /// </summary>
        /// <value>The maximum message body size is 100 MB by default.</value>
        public int MaxMessageBodySize { get; set; } = 100 * 1024 * 1024;
    }
}
