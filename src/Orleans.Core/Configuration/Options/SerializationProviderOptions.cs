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
    }
}
