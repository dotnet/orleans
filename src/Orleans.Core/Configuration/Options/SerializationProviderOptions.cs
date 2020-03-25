using System.Collections.Generic;
using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// Specifies serialization provider and fallback serializer options.
    /// </summary>
    public class SerializationProviderOptions
    {
        /// <summary>
        /// Externally registered serializers
        /// </summary>
        public List<Type> SerializationProviders { get; set; } = new List<Type>();

        /// <summary>
        /// Serializer used if no serializer is found for a type.
        /// </summary>
        public Type FallbackSerializationProvider { get; set; }

        /// <summary>
        /// The maximum retained size for serialization and deserialization contexts.
        /// </summary>
        /// <remarks>
        /// This should reflect the expected object graph size for messages.
        /// </remarks>
        public int MaxSustainedSerializationContextCapacity { get; set; } = 64;
    }
}
