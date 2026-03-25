#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Orleans.GrainReferences;

namespace Orleans.Serialization
{
    /// <summary>
    /// Configures <see cref="JsonSerializerOptions"/> for the System.Text.Json grain storage serialzier
    /// </summary>
    public sealed class SystemTextJsonGrainStorageSerializerOptions
    {
        public JsonSerializerOptions JsonSerializerOptions { get; } = new JsonSerializerOptions()
        {
            // System.Text.Json  => 9.0.0 adds AllowOutOfOrderMetadataProperties
            // which allows ReferenceHandler.Preserve to work with GuidId from Newtonsoft

            // ReferenceHandler = ReferenceHandler.Preserve,
            //  AllowOutOfOrderMetadataProperties = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault | JsonIgnoreCondition.WhenWritingNull,
        };
    }

    public sealed class SystemTextJsonSerializerOptionsConfigure(GrainReferenceActivator grainReferenceActivator) : IPostConfigureOptions<SystemTextJsonGrainStorageSerializerOptions>
    {
        public void PostConfigure(string? name, SystemTextJsonGrainStorageSerializerOptions options)
        {
            var ipAddressConverter = new IpAddressConverter();
            options.JsonSerializerOptions.Converters.Add(new GrainIdJsonConverter());
            options.JsonSerializerOptions.Converters.Add(ipAddressConverter);
            options.JsonSerializerOptions.Converters.Add(new IpEndPointConverter(ipAddressConverter));
            options.JsonSerializerOptions.Converters.Add(new GrainReferenceConverter(grainReferenceActivator));
        }
    }
}
