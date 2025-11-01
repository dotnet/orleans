#nullable enable

using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.Streaming.JsonConverters
{
    internal class StreamingConverterConfigurator : IPostConfigureOptions<OrleansJsonSerializerOptions>
    {
        private readonly IRuntimeClient _runtimeClient;

        public StreamingConverterConfigurator(IRuntimeClient runtimeClient)
        {
            _runtimeClient = runtimeClient;
        }

        public void PostConfigure(string? name, OrleansJsonSerializerOptions options)
        {
            options.JsonSerializerSettings.Converters.Add(new StreamImplConverter(_runtimeClient));
        }
    }

    internal sealed class SystemTextJsonStreamConverterConfigurator(IRuntimeClient runtimeClient) : IPostConfigureOptions<SystemTextJsonGrainStorageSerializerOptions>
    {
        public void PostConfigure(string? name, SystemTextJsonGrainStorageSerializerOptions options)
        {
            options.JsonSerializerOptions.Converters.Add(new AsyncStreamConverter(runtimeClient));
            options.JsonSerializerOptions.Converters.Add(new StreamIdJsonConverter());
            options.JsonSerializerOptions.Converters.Add(new EventSequenceTokenJsonConverter());
            options.JsonSerializerOptions.Converters.Add(new QualifiedStreamIdJsonConverter());
        }
    }
}
