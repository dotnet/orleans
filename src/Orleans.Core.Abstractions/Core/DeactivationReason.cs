using System;

namespace Orleans
{
    /// <summary>
    /// Represents a reason for initiating grain deactivation.
    /// </summary>
    public readonly struct DeactivationReason
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DeactivationReason"/> struct.
        /// </summary>
        /// <param name="code">
        /// The code identifying the deactivation reason.
        /// </param>
        /// <param name="text">
        /// A descriptive reason for the deactivation.
        /// </param>
        public DeactivationReason(DeactivationReasonCode code, string text)
        {
            ReasonCode = code;
            Description = text;
            Exception = null;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DeactivationReason"/> struct.
        /// </summary>
        /// <param name="code">
        /// The code identifying the deactivation reason.
        /// </param>
        /// <param name="exception">
        /// The exception which resulted in deactivation.
        /// </param>
        /// <param name="text">
        /// A descriptive reason for the deactivation.
        /// </param>
        public DeactivationReason(DeactivationReasonCode code, Exception exception, string text)
        {
            ReasonCode = code;
            Description = text;
            Exception = exception;
        }

        /// <summary>
        /// Gets the descriptive reason for the deactivation.
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Gets the reason for deactivation.
        /// </summary>
        public DeactivationReasonCode ReasonCode { get; }

        /// <summary>
        /// Gets the exception which resulted in deactivation.
        /// </summary>
        public Exception Exception { get; }
    }
}
