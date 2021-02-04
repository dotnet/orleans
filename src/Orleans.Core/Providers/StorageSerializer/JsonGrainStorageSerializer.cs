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
        public BinaryData Serialize<T>(T value)
        {
            var data = JsonConvert.SerializeObject(value, this.jsonSettings);
            return new BinaryData(data);
        }

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        public T Deserialize<T>(BinaryData input)
        {
            return JsonConvert.DeserializeObject<T>(input.ToString(), this.jsonSettings);
        }
    }
}
