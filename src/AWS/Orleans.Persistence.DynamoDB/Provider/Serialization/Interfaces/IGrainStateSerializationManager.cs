using System;
using Orleans.Storage;

namespace Orleans.Persistence.DynamoDB.Provider.Serialization.Interfaces;

public interface IGrainStateSerializationManager
{
    /// <summary>
    /// WARN: Do not rename the the enum or the enum values. They are used to mark the binary state properties and they will be stored in DynamoDB along with the state binary data
    /// </summary>
    public enum BinaryStateSerialization
    {
        Json,
        Binary
    }

    void Serialize(object grainState, DynamoDBGrainStorage.GrainStateRecord entity);

    object Deserialize(Type grainStateType, DynamoDBGrainStorage.GrainStateRecord entity);
}