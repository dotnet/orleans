using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents a configuration exception.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class OrleansConfigurationException : Exception
    {
        /// <inheritdoc />
        public OrleansConfigurationException(string message) : base(message) { }

        /// <inheritdoc />
        public OrleansConfigurationException(string message, Exception innerException) : base(message, innerException) { }

        /// <inheritdoc />
        protected OrleansConfigurationException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}