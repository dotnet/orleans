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
        public List<TypeInfo> SerializationProviders { get; set; } = new List<TypeInfo>();
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
                OptionFormattingUtilities.Format(nameof(options.SerializationProviders), string.Join(",", options.SerializationProviders)),
                OptionFormattingUtilities.Format(nameof(options.FallbackSerializationProvider), options.FallbackSerializationProvider)
            };
        }
    }
}
