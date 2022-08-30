using System;
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Exception thrown whenever a provider has failed to be started.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class ProviderStartException : OrleansException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ProviderStartException"/> class.
        /// </summary>
        public ProviderStartException()
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="ProviderStartException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        public ProviderStartException(string message)
            : base(message)
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="ProviderStartException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="innerException">The inner exception.</param>
        public ProviderStartException(string message, Exception innerException)
            : base(message, innerException)
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="ProviderStartException"/> class.
        /// </summary>
        /// <param name="info">The serialization info.</param>
        /// <param name="context">The context.</param>
        private ProviderStartException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }
}
