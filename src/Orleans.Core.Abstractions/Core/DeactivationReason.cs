using System;

namespace Orleans
{
    /// <summary>
    /// Represents a reason for initiating grain deactivation.
    /// </summary>
    public readonly struct DeactivationReason
    {
        public DeactivationReason(DeactivationReasonCode code, string text)
        {
            ReasonCode = code;
            Description = text;
            Exception = null;
        }

        public DeactivationReason(DeactivationReasonCode code, Exception exception, string text)
        {
            ReasonCode = code;
            Description = text;
            Exception = exception;
        }

        /// <summary>
        /// The descriptive reason for the deactivation.
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// The reason for deactivation.
        /// </summary>
        public DeactivationReasonCode ReasonCode { get; }

        /// <summary>
        /// If not null, contains the exception thrown during activation.
        /// </summary>
        public Exception Exception { get; }
    }
}
