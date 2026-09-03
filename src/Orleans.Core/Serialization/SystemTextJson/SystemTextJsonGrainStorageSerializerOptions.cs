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
        /// Gets the serializer options used to read and write grain state.
        /// </summary>
        /// <remarks>
        /// Fields are included and properties with <see langword="null"/> values are omitted by default.
        /// Orleans converters are added during options post-configuration.
        /// </remarks>
        public JsonSerializerOptions JsonSerializerOptions { get; } = new JsonSerializerOptions()
        {
            IncludeFields = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }

    /// <summary>
    /// Adds Orleans framework type converters to <see cref="SystemTextJsonGrainStorageSerializerOptions"/>.
    /// </summary>
    /// <param name="grainReferenceActivator">The activator used by the grain reference converter.</param>
    public sealed class SystemTextJsonSerializerOptionsConfigure(GrainReferenceActivator grainReferenceActivator) : IPostConfigureOptions<SystemTextJsonGrainStorageSerializerOptions>
    {
        /// <inheritdoc />
        public void PostConfigure(string? name, SystemTextJsonGrainStorageSerializerOptions options)
        {
            options.JsonSerializerOptions.Converters.Add(new IPAddressJsonConverter());
            options.JsonSerializerOptions.Converters.Add(new IPEndPointJsonConverter());
            options.JsonSerializerOptions.Converters.Add(new GrainReferenceConverter(grainReferenceActivator));
        }
    }
}
