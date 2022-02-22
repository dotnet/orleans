using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Indicates a lifecycle was canceled, either by request or due to observer error.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class OrleansLifecycleCanceledException : OrleansException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansLifecycleCanceledException"/> class.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        internal OrleansLifecycleCanceledException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansLifecycleCanceledException"/> class.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        /// <param name="innerException">
        /// The inner exception.
        /// </param>
        internal OrleansLifecycleCanceledException(string message,
                                                   Exception innerException) : base(message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansLifecycleCanceledException"/> class.
        /// </summary>
        /// <param name="info">
        /// The serialization info.
        /// </param>
        /// <param name="context">
        /// The context.
        /// </param>
        /// <exception cref="SerializationException">The class name is <see langword="null" /> or <see cref="P:System.Exception.HResult" /> is zero (0).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="info" /> is <see langword="null" />.</exception>
        protected OrleansLifecycleCanceledException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}

