using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Options;

namespace Orleans.Hosting
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

    public class SerializationProviderOptionsFormatter : IOptionFormatter<SerializationProviderOptions>
    {
        public string Category { get; }

        public string Name => nameof(SerializationProviderOptions);
        private SerializationProviderOptions options;
        public SerializationProviderOptionsFormatter(IOptions<SerializationProviderOptions> options)
        {
            this.options = options.Value;
        }

        public IEnumerable<string> Format()
        {
            return new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(this.options.SerializationProviders), string.Join(",", this.options.SerializationProviders)),
                OptionFormattingUtilities.Format(nameof(this.options.FallbackSerializationProvider), this.options.FallbackSerializationProvider)
            };
        }
    }
}
