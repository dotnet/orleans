using System;
using System.Diagnostics;

namespace Orleans.Configuration
{
    /// <summary>
    /// Specifies global messaging options that are common to client and silo.
    /// </summary>
    public abstract class MessagingOptions
    {
        /// <summary>
        /// The ResponseTimeout attribute specifies the default timeout before a request is assumed to have failed.
        /// </summary>
        public TimeSpan ResponseTimeout { get; set; } = DEFAULT_RESPONSE_TIMEOUT;
        public static readonly TimeSpan DEFAULT_RESPONSE_TIMEOUT = Debugger.IsAttached ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(30);

        /// <summary>
        /// The MaxResendCount attribute specifies the maximal number of resends of the same message.
        /// </summary>
        public int MaxResendCount { get; set; }

        /// <summary>
        /// The ResendOnTimeout attribute specifies whether the message should be automaticaly resend by the runtime when it times out on the sender.
        /// Default is false.
        /// </summary>
        public bool ResendOnTimeout { get; set; }

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
    }
}
