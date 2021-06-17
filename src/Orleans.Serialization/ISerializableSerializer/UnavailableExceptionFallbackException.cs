using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Serialization;

namespace Orleans.Serialization
{
    /// <summary>
    /// Represents an exception which has a type which is unavailable during deserialization.
    /// </summary>
    [DebuggerDisplay("{" + nameof(GetDebuggerDisplay) + "(),nq}")]
    public sealed class UnavailableExceptionFallbackException : Exception
    {
        /// <inheritdoc />
        public UnavailableExceptionFallbackException()
        {
        }

        /// <inheritdoc />
        public UnavailableExceptionFallbackException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
            foreach (var pair in info)
            {
                Properties[pair.Name] = pair.Value;
            }
        }

        /// <inheritdoc />
        public UnavailableExceptionFallbackException(string message, Exception innerException) : base(message, innerException)
        {
        }

        /// <summary>
        /// Gets the serialized properties of the exception.
        /// </summary>
        public Dictionary<string, object> Properties { get; } = new();

        /// <summary>
        /// Gets the exception type name.
        /// </summary>
        public string ExceptionType { get; internal set; }

        /// <inheritdoc />
        public override string ToString() => string.IsNullOrWhiteSpace(ExceptionType) ? $"Unknown exception: {base.ToString()}" : $"Unknown exception of type {ExceptionType}: {base.ToString()}";

        private string GetDebuggerDisplay() => ToString();
    }
}