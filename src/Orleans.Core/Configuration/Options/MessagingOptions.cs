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
        /// The ResponseTimeout attribute specifies the default timeout before a request is assumed to have failed.
        ///<seealso cref="ResponseTimeoutWithDebugger"/>
        /// </summary>
        public TimeSpan ResponseTimeout
        {
            get { return Debugger.IsAttached ? ResponseTimeoutWithDebugger : responseTimeout; }
            set { this.responseTimeout = value; }
        }
        public static readonly TimeSpan DEFAULT_RESPONSE_TIMEOUT = TimeSpan.FromSeconds(30);
        private TimeSpan responseTimeout = DEFAULT_RESPONSE_TIMEOUT;

        /// <summary>
        /// If a debugger is attached the value from <see cref="ResponseTimeout"/> will be ignored 
        /// and the value from this field will be used.
        /// </summary>
        public TimeSpan ResponseTimeoutWithDebugger { get; set; } = RESPONSE_TIMEOUT_WITH_DEBUGGER;
        public static readonly TimeSpan RESPONSE_TIMEOUT_WITH_DEBUGGER = TimeSpan.FromMinutes(30);

        /// <summary>
        /// The DropExpiredMessages attribute specifies whether the message should be dropped if it has expired, that is if it was not delivered 
        /// to the destination before it has timed out on the sender.
        /// Default is true.
        /// </summary>
        public bool DropExpiredMessages { get; set; } = DEFAULT_DROP_EXPIRED_MESSAGES;
        public const bool DEFAULT_DROP_EXPIRED_MESSAGES = true;

        /// <summary>
        /// The size of a buffer in the messaging buffer pool.
        /// </summary>
        public int BufferPoolBufferSize { get; set; } = DEFAULT_BUFFER_POOL_BUFFER_SIZE;
        public const int DEFAULT_BUFFER_POOL_BUFFER_SIZE = 4 * 1024;

        /// <summary>
        /// The maximum size of the messaging buffer pool.
        /// </summary>
        public int BufferPoolMaxSize { get; set; } = DEFAULT_BUFFER_POOL_MAX_SIZE;
        public const int DEFAULT_BUFFER_POOL_MAX_SIZE = 10000;

        /// <summary>
        /// The initial size of the messaging buffer pool that is pre-allocated.
        /// </summary>
        public int BufferPoolPreallocationSize { get; set; } = DEFAULT_BUFFER_POOL_PREALLOCATION_SIZE;
        public const int DEFAULT_BUFFER_POOL_PREALLOCATION_SIZE = 250;
        
        /// <summary>
        ///  Whether Trace.CorrelationManager.ActivityId settings should be propagated into grain calls.
        /// </summary>
        public bool PropagateActivityId { get; set; } = DEFAULT_PROPAGATE_E2E_ACTIVITY_ID;
        public const bool DEFAULT_PROPAGATE_E2E_ACTIVITY_ID = false;

        /// <summary>
        /// The LargeMessageWarningThreshold attribute specifies when to generate a warning trace message for large messages.
        /// </summary>
        public int LargeMessageWarningThreshold { get; set; } = DEFAULT_LARGE_MESSAGE_WARNING_THRESHOLD;
        public const int DEFAULT_LARGE_MESSAGE_WARNING_THRESHOLD = Constants.LARGE_OBJECT_HEAP_THRESHOLD;

        /// <summary>
        /// The maximum number of times a message send attempt will be retried.
        /// </summary>
        internal const int DEFAULT_MAX_MESSAGE_SEND_RETRIES = 1;

        /// <summary>
        /// The maximum size, in bytes, of the header for a message. The runtime will forcibly close the connection
        /// if the header size is greater than this value.
        /// </summary>
        public int MaxMessageHeaderSize { get; set; } = DEFAULT_MAX_MESSAGE_HEADER_SIZE;
        public const int DEFAULT_MAX_MESSAGE_HEADER_SIZE = 10485760; // 10MB

        /// <summary>
        /// The maximum size, in bytes, of the body for a message. The runtime will forcibly close the connection
        /// if the body size is greater than this value.
        /// </summary>
        public int MaxMessageBodySize { get; set; } = DEFAULT_MAX_MESSAGE_BODY_SIZE;
        public const int DEFAULT_MAX_MESSAGE_BODY_SIZE = 104857600; // 100MB
    }
}
