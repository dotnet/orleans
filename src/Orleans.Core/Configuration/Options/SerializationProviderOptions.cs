using System.Collections.Generic;
using System.Reflection;

namespace Orleans.Hosting
{
    /// <summary>
    /// Specifies serialization provider and fallback serializer options.
    /// </summary>
    public class SerializationProviderOptions
    {
        public List<TypeInfo> SerializationProviders { get; set; }
        public TypeInfo FallbackSerializationProvider { get; set; }
    }
}
