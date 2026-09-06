using System;
using System.Collections.Generic;

namespace Orleans
{
    /// <summary>
    /// Represents a reason for initiating grain deactivation.
    /// </summary>
    public readonly struct DeactivationReason : IEquatable<DeactivationReason>
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
        public DeactivationReason(DeactivationReasonCode code, Exception? exception, string text)
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
        public Exception? Exception { get; }

        /// <inheritdoc/>
        public bool Equals(DeactivationReason other) =>
            ReasonCode == other.ReasonCode
            && Description == other.Description
            && EqualityComparer<Exception?>.Default.Equals(Exception, other.Exception);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is DeactivationReason other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(ReasonCode, Description, Exception);

        /// <summary>
        /// Determines whether two deactivation reasons are equal.
        /// </summary>
        public static bool operator ==(DeactivationReason left, DeactivationReason right) => left.Equals(right);

        /// <summary>
        /// Determines whether two deactivation reasons are unequal.
        /// </summary>
        public static bool operator !=(DeactivationReason left, DeactivationReason right) => !left.Equals(right);

        /// <inheritdoc/>
        public override string ToString()
        {
            if (Exception is not null)
            {
                return $"{ReasonCode}: {Description}. Exception: {Exception}";
            }

            return $"{ReasonCode}: {Description}";
        }
    }
}
