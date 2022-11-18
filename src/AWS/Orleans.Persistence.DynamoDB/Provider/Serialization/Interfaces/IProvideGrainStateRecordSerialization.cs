using System;
using static Orleans.Storage.DynamoDBGrainStorage;

namespace Orleans.Persistence.DynamoDB.Provider.Serialization.Interfaces
{
    public interface IProvideGrainStateRecordSerialization
    {
        public IGrainStateSerializationManager.BinaryStateSerialization SerializationType { get; }

        void Serialize(object grainState, GrainStateRecord entity);

        object Deserialize(Type grainStateType, GrainStateRecord entity);
    }
}
