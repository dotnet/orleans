using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents a configuration exception.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class OrleansConfigurationException : Exception
    {
        /// <inheritdoc />
        public OrleansConfigurationException(string message)
            : base(message)
        {
        }

        /// <inheritdoc />
        public OrleansConfigurationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <inheritdoc />
        /// <exception cref="SerializationException">The class name is <see langword="null" /> or <see cref="P:System.Exception.HResult" /> is zero (0).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="info" /> is <see langword="null" />.</exception>
        private OrleansConfigurationException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}