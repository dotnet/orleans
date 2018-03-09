using System.Collections.Generic;
using System.Reflection;

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
        public List<TypeInfo> SerializationProviders { get; set; } = new List<TypeInfo>();

        /// <summary>
        /// Serializer used if no serializer is found for a type.
        /// </summary>
        public TypeInfo FallbackSerializationProvider { get; set; }
    }
}
