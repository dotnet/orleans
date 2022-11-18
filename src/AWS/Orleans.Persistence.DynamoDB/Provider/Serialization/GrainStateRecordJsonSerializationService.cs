using System;
using System.Text;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Persistence.DynamoDB.Provider.Serialization.Interfaces;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;

namespace Orleans.Persistence.DynamoDB.Provider.Serialization;

public class GrainStateRecordJsonSerializationService : IProvideGrainStateRecordSerialization
{
    private readonly JsonSerializerSettings jsonSettings;

    public GrainStateRecordJsonSerializationService(
        ITypeResolver typeResolver,
        IGrainFactory grainFactory,
        DynamoDBStorageOptions options)
    {
        this.jsonSettings = OrleansJsonSerializer.UpdateSerializerSettings(
            OrleansJsonSerializer.GetDefaultSerializerSettings(typeResolver, grainFactory),
            options.UseFullAssemblyNames,
            options.IndentJson,
            options.TypeNameHandling);

        options.ConfigureJsonSerializerSettings?.Invoke(this.jsonSettings);
        
        this.SerializationType = IGrainStateSerializationManager.BinaryStateSerialization.Json;
    }

    public IGrainStateSerializationManager.BinaryStateSerialization SerializationType { get; }

    public void Serialize(object grainState, DynamoDBGrainStorage.GrainStateRecord entity)
    {
        entity.BinaryState = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(grainState, this.jsonSettings));
        entity.BinaryStateProperties.Add(GrainStateSerializationManager.SerializationPropertyName, this.SerializationType.ToString());
    }

    public object Deserialize(Type grainStateType, DynamoDBGrainStorage.GrainStateRecord entity)
    {
        if (!entity.BinaryStateProperties.ContainsKey(GrainStateSerializationManager.SerializationPropertyName))
        {
            throw new ArgumentException("Grain state has not been serialized using JSON serialization", nameof(entity));
        }

        return JsonConvert.DeserializeObject(Encoding.UTF8.GetString(entity.BinaryState), grainStateType, this.jsonSettings);
    }
}