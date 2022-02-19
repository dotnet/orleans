using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Signifies that an request was canceled due to target silo unavailability.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class SiloUnavailableException : OrleansMessageRejectionException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SiloUnavailableException"/> class.
        /// </summary>
        public SiloUnavailableException()
            : base("Silo unavailable")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SiloUnavailableException"/> class.
        /// </summary>
        /// <param name="msg">
        /// The msg.
        /// </param>
        public SiloUnavailableException(string msg)
            : base(msg)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SiloUnavailableException"/> class.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        /// <param name="innerException">
        /// The inner exception.
        /// </param>
        public SiloUnavailableException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SiloUnavailableException"/> class.
        /// </summary>
        /// <param name="info">
        /// The info.
        /// </param>
        /// <param name="context">
        /// The context.
        /// </param>
        /// <exception cref="SerializationException">The class name is <see langword="null" /> or <see cref="P:System.Exception.HResult" /> is zero (0).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="info" /> is <see langword="null" />.</exception>
        protected SiloUnavailableException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}

