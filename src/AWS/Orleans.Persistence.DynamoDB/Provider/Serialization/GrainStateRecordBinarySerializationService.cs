using System;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Persistence.DynamoDB.Provider.Serialization.Interfaces;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;

namespace Orleans.Persistence.DynamoDB.Provider.Serialization;

public class GrainStateRecordBinarySerializationService : IProvideGrainStateRecordSerialization
{
    private readonly ILogger<GrainStateRecordBinarySerializationService> logger;
    private readonly JsonSerializerSettings jsonSettings;

    public GrainStateRecordBinarySerializationService(
        ITypeResolver typeResolver,
        IGrainFactory grainFactory,
        DynamoDBStorageOptions options,
        ILogger<GrainStateRecordBinarySerializationService> logger)
    {
        this.jsonSettings = OrleansJsonSerializer.UpdateSerializerSettings(
            OrleansJsonSerializer.GetDefaultSerializerSettings(typeResolver, grainFactory),
            options.UseFullAssemblyNames,
            options.IndentJson,
            options.TypeNameHandling);

        options.ConfigureJsonSerializerSettings?.Invoke(this.jsonSettings);
        this.logger = logger;
        
        this.SerializationType = IGrainStateSerializationManager.BinaryStateSerialization.Binary;
    }

    public IGrainStateSerializationManager.BinaryStateSerialization SerializationType { get; }

    public void Serialize(object grainState, DynamoDBGrainStorage.GrainStateRecord record)
    {
        if (record?.BinaryStateProperties is null)
        {
            throw new ArgumentException("record or record.StateProperties is null", nameof(record));
        }

        if (record.BinaryStateProperties.ContainsKey(GrainStateSerializationManager.SerializationPropertyName))
        {
            this.logger.LogWarning("State properties already contains the compression property {0}", GrainStateSerializationManager.SerializationPropertyName);
            return;
        }

        record.BinaryState = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(grainState, this.jsonSettings));
        record.BinaryStateProperties.Add(GrainStateSerializationManager.SerializationPropertyName, this.SerializationType.ToString());
    }

    public object Deserialize(Type grainStateType, DynamoDBGrainStorage.GrainStateRecord record)
    {
        if (record?.BinaryStateProperties is null)
        {
            throw new ArgumentException("record or record.StateProperties is null", nameof(record));
        }

        if (!record.BinaryStateProperties.TryGetValue(GrainStateSerializationManager.SerializationPropertyName, out var binaryStateCompressionValue)
            || binaryStateCompressionValue != this.SerializationType.ToString())
        {
            throw new ArgumentException($"Grain state has not been serialized using serialization {GrainStateSerializationManager.SerializationPropertyName}={this.SerializationType}");
        }

        return JsonConvert.DeserializeObject(Encoding.UTF8.GetString(record.BinaryState), grainStateType, this.jsonSettings);
    }
}