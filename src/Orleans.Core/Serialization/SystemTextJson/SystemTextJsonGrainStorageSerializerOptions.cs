#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Orleans.GrainReferences;

namespace Orleans.Serialization
{
    /// <summary>
    /// Configures <see cref="JsonSerializerOptions"/> for the System.Text.Json grain storage serializer.
    /// </summary>
    public sealed class SystemTextJsonGrainStorageSerializerOptions
    {
        /// <summary>
        /// Gets the underlying System.Text.Json serializer options.
        /// </summary>
        public JsonSerializerOptions JsonSerializerOptions { get; } = new JsonSerializerOptions()
        {
            WriteIndented = true,
            IncludeFields = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }

    public sealed class SystemTextJsonSerializerOptionsConfigure(GrainReferenceActivator grainReferenceActivator) : IPostConfigureOptions<SystemTextJsonGrainStorageSerializerOptions>
    {
        public void PostConfigure(string? name, SystemTextJsonGrainStorageSerializerOptions options)
        {
            var ipAddressConverter = new IPAddressJsonConverter();
            options.JsonSerializerOptions.Converters.Add(new GrainIdJsonConverter());
            options.JsonSerializerOptions.Converters.Add(ipAddressConverter);
            options.JsonSerializerOptions.Converters.Add(new IPEndPointJsonConverter(ipAddressConverter));
            options.JsonSerializerOptions.Converters.Add(new GrainReferenceConverter(grainReferenceActivator));
        }
    }
}
