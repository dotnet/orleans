using System;
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.Providers
{
    /// <summary>
    /// Exception thrown whenever a provider has failed to be initialized.
    /// </summary>
    [Serializable, GenerateSerializer]
    public sealed class ProviderInitializationException : OrleansException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ProviderInitializationException"/> class.
        /// </summary>
        public ProviderInitializationException()
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="ProviderInitializationException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        public ProviderInitializationException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ProviderInitializationException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="innerException">The inner exception.</param>
        public ProviderInitializationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ProviderInitializationException"/> class.
        /// </summary>
        /// <param name="info">The serialization info.</param>
        /// <param name="context">The context.</param>
        private ProviderInitializationException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
