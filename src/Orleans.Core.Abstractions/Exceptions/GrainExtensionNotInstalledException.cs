using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Signifies that an attempt was made to invoke a grain extension method on a grain where that extension was not installed.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class GrainExtensionNotInstalledException : OrleansException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GrainExtensionNotInstalledException"/> class.
        /// </summary>
        public GrainExtensionNotInstalledException()
            : base("GrainExtensionNotInstalledException")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainExtensionNotInstalledException"/> class.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        public GrainExtensionNotInstalledException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainExtensionNotInstalledException"/> class.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        /// <param name="innerException">
        /// The inner exception.
        /// </param>
        public GrainExtensionNotInstalledException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainExtensionNotInstalledException"/> class.
        /// </summary>
        /// <param name="info">
        /// The serialization info.
        /// </param>
        /// <param name="context">
        /// The context.
        /// </param>
        protected GrainExtensionNotInstalledException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}

