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
        /// The MaxResendCount attribute specifies the maximal number of resends of the same message.
        /// </summary>
        public int MaxResendCount { get; set; }

        /// <summary>
        /// The ResendOnTimeout attribute specifies whether the message should be automatically resend by the runtime when it times out on the sender.
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
        /// The minimum size of a buffer in the messaging buffer pool.
        /// </summary>
        public int BufferPoolMinimumBufferSize { get; set; } = DEFAULT_BUFFER_POOL_MINIMUM_BUFFER_SIZE;
        public const int DEFAULT_BUFFER_POOL_MINIMUM_BUFFER_SIZE = 1 + (4095 / sizeof(byte));
        
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
    }
}
