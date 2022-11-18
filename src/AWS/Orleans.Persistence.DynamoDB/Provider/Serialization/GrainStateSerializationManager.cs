using System;
using Orleans.Persistence.DynamoDB.Provider.Serialization.Interfaces;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using System.Collections.Generic;
using System.Linq;
using Orleans.Storage;

namespace Orleans.Persistence.DynamoDB.Provider.Serialization
{
    internal class GrainStateSerializationManager : IGrainStateSerializationManager
    {
        public const string SerializationPropertyName = "Serialization";
        
        private readonly Dictionary<IGrainStateSerializationManager.BinaryStateSerialization, IProvideGrainStateRecordSerialization> serializationProviders;
        private readonly IProvideGrainStateRecordSerialization serializationProviderConfigured;
        private readonly ILogger<GrainStateSerializationManager> logger;

        public GrainStateSerializationManager(
            DynamoDBStorageOptions options,
            IEnumerable<IProvideGrainStateRecordSerialization> serializationProviders,
            ILogger<GrainStateSerializationManager> logger)
        {
            this.logger = logger;

            this.serializationProviders = serializationProviders.ToDictionary(
                serializationProvider => serializationProvider.SerializationType);

            this.serializationProviderConfigured =
                this.serializationProviders[options.UseJson
                    ? IGrainStateSerializationManager.BinaryStateSerialization.Json
                    : IGrainStateSerializationManager.BinaryStateSerialization.Binary];
        }

        public void Serialize(object grainState, DynamoDBGrainStorage.GrainStateRecord record)
        {
            if (record?.BinaryStateProperties == null)
            {
                throw new ArgumentException("record or record.StateProperties is null", nameof(record));
            }

            if (record.BinaryStateProperties.ContainsKey(SerializationPropertyName))
            {
                this.logger.LogWarning("State properties already contains the serialization property {0}", SerializationPropertyName);
                return;
            }
            
            this.serializationProviderConfigured.Serialize(grainState, record);
        }

        public object Deserialize(Type grainStateType, DynamoDBGrainStorage.GrainStateRecord record)
        {
            if (!record.BinaryStateProperties.TryGetValue(
                    SerializationPropertyName,
                    out var recordSerializationTypeProperty))
            {
                throw new ArgumentException("Record properties map does not have the serialization property", nameof(record));
            }

            if (!Enum.TryParse<IGrainStateSerializationManager.BinaryStateSerialization>(
                    recordSerializationTypeProperty,
                    true,
                    out var recordSerializationType))
            {
                throw new ArgumentException($"Unable to parse the record serialization property to {nameof(IGrainStateSerializationManager.BinaryStateSerialization)}");
            }

            return this.serializationProviders.TryGetValue(recordSerializationType, out var recordSerializationProvider)
                ? recordSerializationProvider.Deserialize(grainStateType, record)
                : throw new ArgumentException($"Record serialization type has not been registered: {recordSerializationType}", nameof(record));
        }
    }
}
