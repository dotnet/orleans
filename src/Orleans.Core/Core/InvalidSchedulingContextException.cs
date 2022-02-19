using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{

    /// <summary>
    /// Signifies that an operation was attempted on an invalid SchedulingContext.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    internal sealed class InvalidSchedulingContextException : OrleansException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="InvalidSchedulingContextException"/> class.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        public InvalidSchedulingContextException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="InvalidSchedulingContextException"/> class.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        /// <param name="innerException">
        /// The inner exception.
        /// </param>
        public InvalidSchedulingContextException(string message, Exception innerException) : base(message, innerException) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="InvalidSchedulingContextException"/> class.
        /// </summary>
        /// <param name="info">
        /// The serialization info.
        /// </param>
        /// <param name="context">
        /// The context.
        /// </param>
        private InvalidSchedulingContextException(SerializationInfo info, StreamingContext context) : base(info, context) { }
    }
}

