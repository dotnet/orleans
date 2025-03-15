using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Orleans.GrainReferences;

#nullable enable

namespace Orleans.Serialization
{

    /// <summary>
    /// Configures <see cref="JsonSerializerOptions"/> for the System.Text.Json grain storage serialzier
    /// </summary>
    public sealed class SystemTextJsonGrainStorageSerializerOptions
    {
        public JsonSerializerOptions JsonSerializerOptions { get; } = new JsonSerializerOptions()
        {
            ReferenceHandler = ReferenceHandler.Preserve,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault | JsonIgnoreCondition.WhenWritingNull,
        };
    }

    public sealed class SystemTextJsonSerializerOptionsConfigure(GrainReferenceActivator grainReferenceActivator) : IPostConfigureOptions<SystemTextJsonGrainStorageSerializerOptions>
    {
        public void PostConfigure(string? name, SystemTextJsonGrainStorageSerializerOptions options)
        {
            options.JsonSerializerOptions.Converters.Add(new GrainIdJsonConverter());
            options.JsonSerializerOptions.Converters.Add(new IpAddressConverter());
            options.JsonSerializerOptions.Converters.Add(new ActivationIdJsonConverter());
            options.JsonSerializerOptions.Converters.Add(new SiloAddressConverter());
            options.JsonSerializerOptions.Converters.Add(new MembershipVersionConverter());
            options.JsonSerializerOptions.Converters.Add(new UniqueKeyJsonConverter());
            options.JsonSerializerOptions.Converters.Add(new IpEndPointConverter());
            options.JsonSerializerOptions.Converters.Add(new GrainReferenceConverter(grainReferenceActivator));
        }
    }
}