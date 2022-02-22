using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Serialization;

namespace Orleans.Storage
{
    /// <summary>
    /// Options for <see cref="JsonGrainStorageSerializer"/>.
    /// </summary>
    public class JsonGrainStorageSerializerOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether to serialize values using their full assembly-qualified type names.
        /// </summary>
        public bool UseFullAssemblyNames { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to indent serialized JSON payloads.
        /// </summary>
        public bool IndentJson { get; set; }

        /// <summary>
        /// Gets or sets the type name handling strategy.
        /// </summary>
        public TypeNameHandling? TypeNameHandling { get; set; }

        /// <summary>
        /// Gets or sets a delegate used to configure <see cref="JsonSerializerSettings"/> after other options have been applied.
        /// </summary>
        public Action<JsonSerializerSettings> ConfigureJsonSerializerSettings { get; set; }
    }

    /// <summary>
    /// Grain storage serializer that uses Newtonsoft.Json
    /// </summary>
    public class JsonGrainStorageSerializer : IGrainStorageSerializer
    {
        private JsonSerializerSettings jsonSettings;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonGrainStorageSerializer"/> class.
        /// </summary>
        /// <param name="options">The serializer options.</param>
        /// <param name="services">The service provider.</param>
        public JsonGrainStorageSerializer(IOptions<JsonGrainStorageSerializerOptions> options, IServiceProvider services)
        {
            this.jsonSettings = OrleansJsonSerializer.UpdateSerializerSettings(
                OrleansJsonSerializer.GetDefaultSerializerSettings(services),
                options.Value.UseFullAssemblyNames,
                options.Value.IndentJson,
                options.Value.TypeNameHandling);

            options.Value.ConfigureJsonSerializerSettings?.Invoke(this.jsonSettings);
        }

        /// <inheritdoc/>
        public BinaryData Serialize<T>(T value)
        {
            var data = JsonConvert.SerializeObject(value, this.jsonSettings);
            return new BinaryData(data);
        }

        /// <inheritdoc/>
        public T Deserialize<T>(BinaryData input)
        {
            return JsonConvert.DeserializeObject<T>(input.ToString(), this.jsonSettings);
        }
    }
}
