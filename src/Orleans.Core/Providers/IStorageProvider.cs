using System;
using System.Runtime.Serialization;
using Orleans.Runtime;

namespace Orleans.Storage
{
    /// <summary>
    /// Exception thrown whenever a grain call is attempted with a bad / missing storage provider configuration settings for that grain.
    /// </summary>
    [Serializable, GenerateSerializer]
    public sealed class BadProviderConfigException : OrleansException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="BadProviderConfigException"/> class.
        /// </summary>
        public BadProviderConfigException()
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="BadProviderConfigException"/> class.
        /// </summary>
        /// <param name="msg">The message that describes the error.</param>
        public BadProviderConfigException(string msg)
            : base(msg)
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="BadProviderConfigException"/> class.
        /// </summary>
        /// <param name="msg">The message that describes the error.</param>
        /// <param name="exc">The exception that caused the current exception.</param>
        public BadProviderConfigException(string msg, Exception exc)
            : base(msg, exc)
        { }

        [Obsolete]
        private BadProviderConfigException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        { }
    }
}
