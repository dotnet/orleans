using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Hosting
{
    /// <summary>
    /// Specifies global messaging options that are common to client and silo.
    /// </summary>
    public abstract class MessagingOptions
    {
        /// <summary>
        /// The ResponseTimeout attribute specifies the default timeout before a request is assumed to have failed.
        /// </summary>
        public TimeSpan ResponseTimeout { get; set; } = Constants.DEFAULT_RESPONSE_TIMEOUT;

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
        public bool DropExpiredMessages { get; set; } = true;

        /// <summary>
        /// The size of a buffer in the messaging buffer pool.
        /// </summary>
        public int BufferPoolBufferSize { get; set; } = 4 * 1024;

        /// <summary>
        /// The maximum size of the messaging buffer pool.
        /// </summary>
        public int BufferPoolMaxSize { get; set; } = 10000;

        /// <summary>
        /// The initial size of the messaging buffer pool that is pre-allocated.
        /// </summary>
        public int BufferPoolPreallocationSize { get; set; } = 250;

        /// <summary>
        ///  Whether Trace.CorrelationManager.ActivityId settings should be propagated into grain calls.
        /// </summary>
        public bool PropagateActivityId { get; set; } = DEFAULT_PROPAGATE_ACTIVITY_ID;
        public const bool DEFAULT_PROPAGATE_ACTIVITY_ID = Constants.DEFAULT_PROPAGATE_E2E_ACTIVITY_ID;
    }

    public abstract class MessagingOptionsFormatter
    {
        private MessagingOptions options;

        protected MessagingOptionsFormatter(MessagingOptions options)
        {
            this.options = options;
        }

        protected List<string> FormatSharedOptions()
        {
            return new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(this.options.ResponseTimeout), this.options.ResponseTimeout),
                OptionFormattingUtilities.Format(nameof(this.options.MaxResendCount), this.options.MaxResendCount),
                OptionFormattingUtilities.Format(nameof(this.options.ResendOnTimeout), this.options.ResendOnTimeout),
                OptionFormattingUtilities.Format(nameof(this.options.DropExpiredMessages), this.options.DropExpiredMessages),
                OptionFormattingUtilities.Format(nameof(this.options.BufferPoolBufferSize), this.options.BufferPoolBufferSize),
                OptionFormattingUtilities.Format(nameof(this.options.BufferPoolMaxSize), this.options.BufferPoolMaxSize),
                OptionFormattingUtilities.Format(nameof(this.options.BufferPoolPreallocationSize), this.options.BufferPoolPreallocationSize),
                OptionFormattingUtilities.Format(nameof(this.options.PropagateActivityId), this.options.PropagateActivityId),
            };
        }
    }

}
