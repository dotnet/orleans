using System;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Orleans.Serialization
{
    public class OrleansJsonSerializerOptions
    {
        public JsonSerializerSettings JsonSerializerSettings { get; set; }

        public OrleansJsonSerializerOptions() => JsonSerializerSettings = OrleansJsonSerializerSettings.GetDefaultSerializerSettings();
    }

    public class ConfigureOrleansJsonSerializerOptions(IServiceProvider serviceProvider) : IPostConfigureOptions<OrleansJsonSerializerOptions>
    {
        public void PostConfigure(string name, OrleansJsonSerializerOptions options)
        {
            OrleansJsonSerializerSettings.Configure(serviceProvider, options.JsonSerializerSettings);
        }
    }
}
