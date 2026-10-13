using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using Orleans.Providers.MongoDB.StorageProviders.Serializers.BsonSerializationProviders;

namespace Orleans.Providers.MongoDB.StorageProviders.Serializers;

/// <summary>
/// Stores queryable grain-state documents using MongoDB BSON serializers and class maps.
/// </summary>
public class BsonGrainStateSerializer : IGrainStateSerializer
{
    /// <summary>
    /// Registers the Orleans BSON provider once for the process.
    /// </summary>
    static BsonGrainStateSerializer()
    {
        BsonSerializer.RegisterSerializationProvider(new OrleansBsonSerializationProvider());
    }

    /// <inheritdoc />
    public BsonValue Serialize<T>(T state)
    {
        return BsonDocumentWrapper.Create(state);
    }

    /// <inheritdoc />
    public T Deserialize<T>(BsonValue value)
    {
        if (value.IsBsonDocument)
        {
            return BsonSerializer.Deserialize<T>(value.AsBsonDocument);
        }
        else
        {
            // A primitive types is not a valid BsonDocument, so it is wrapped here.
            var document = new BsonDocument("value", value);
            var wrapper = BsonSerializer.Deserialize<ValueWrapper<T>>(document);
            return wrapper.Value;
        }
    }

    private class ValueWrapper<T>
    {
        [BsonElement("value")]
        public T Value { get; set; } = default!;
    }
}
