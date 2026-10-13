using System;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.Providers.MongoDB.Configuration;
using Orleans.Serialization;

namespace Orleans.Providers.MongoDB.StorageProviders.Serializers;

/// <summary>
/// Stores grain state as BSON documents using Orleans' Newtonsoft.Json settings.
/// </summary>
public class JsonGrainStateSerializer : IGrainStateSerializer
{
    private readonly JsonSerializerSettings _jsonSettings;

    /// <summary>
    /// Initializes the serializer using Orleans JSON services and application settings.
    /// </summary>
    public JsonGrainStateSerializer(IOptions<JsonGrainStateSerializerOptions> options, IServiceProvider serviceProvider)
    {
        _jsonSettings = OrleansJsonSerializerSettings.GetDefaultSerializerSettings(serviceProvider);
        options.Value.ConfigureJsonSerializerSettings(_jsonSettings);
    }

    /// <inheritdoc />
    public T Deserialize<T>(BsonValue value)
    {
        using var jsonReader = new JTokenReader(value.ToJToken());
        // Creating a new serializer instance to avoid thread-safety issue: https://github.com/JamesNK/Newtonsoft.Json/issues/1452
        var jsonSerializer = JsonSerializer.CreateDefault(_jsonSettings);
        return jsonSerializer.Deserialize<T>(jsonReader)!;
    }

    /// <inheritdoc />
    public BsonValue Serialize<T>(T state)
    {
        var jsonSerializer = JsonSerializer.CreateDefault(_jsonSettings);
        ArgumentNullException.ThrowIfNull(state);
        return JObject.FromObject(state, jsonSerializer).ToBson();
    }
}
