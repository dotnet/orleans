using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Serialization;

namespace Orleans.Storage
{
    public class JsonGrainStorageSerializerOptions
    {
        public bool UseFullAssemblyNames { get; set; }
        public bool IndentJson { get; set; }
        public TypeNameHandling? TypeNameHandling { get; set; }
        public Action<JsonSerializerSettings> ConfigureJsonSerializerSettings { get; set; }
    }

    /// <summary>
    /// Grain storage serializer that uses Newtonsoft.Json
    /// </summary>
    public class JsonGrainStorageSerializer : IGrainStorageSerializer
    {
        private static List<string> supportedTags => new List<string> { WellKnownSerializerTag.Json };

        private JsonSerializerSettings jsonSettings;

        public JsonGrainStorageSerializer(IOptions<JsonGrainStorageSerializerOptions> options, IServiceProvider services)
        {
            this.jsonSettings = OrleansJsonSerializer.UpdateSerializerSettings(
                OrleansJsonSerializer.GetDefaultSerializerSettings(services),
                options.Value.UseFullAssemblyNames,
                options.Value.IndentJson,
                options.Value.TypeNameHandling);

            options.Value.ConfigureJsonSerializerSettings?.Invoke(this.jsonSettings);
        }

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public List<string> SupportedTags => supportedTags;

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public string Serialize<T>(T value, out BinaryData output)
        {
            var data = JsonConvert.SerializeObject(value, this.jsonSettings);
            output = new BinaryData(data);
            return WellKnownSerializerTag.Json;
        }

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public T Deserialize<T>(BinaryData input, string tag)
        {
            if (!tag.Equals(WellKnownSerializerTag.Json, StringComparison.InvariantCultureIgnoreCase))
            {
                throw new ArgumentException($"Unsupported tag '{tag}'", nameof(tag));
            }

            return JsonConvert.DeserializeObject(input.ToString(), expected, this.jsonSettings);
        }
    }
}
