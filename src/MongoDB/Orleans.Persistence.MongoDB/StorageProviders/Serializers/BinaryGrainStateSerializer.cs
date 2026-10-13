using MongoDB.Bson;
using Orleans.Serialization;
using Orleans.Storage;

namespace Orleans.Providers.MongoDB.StorageProviders.Serializers;

/// <summary>
/// Stores Orleans-serialized grain-state bytes in a BSON document.
/// </summary>
public sealed class BinaryGrainStateSerializer : IGrainStateSerializer
{
    private const string BinaryElementName = "data";

    private readonly OrleansGrainStorageSerializer serializer;

    /// <summary>
    /// Initializes the serializer using the application's Orleans serialization services.
    /// </summary>
    public BinaryGrainStateSerializer(Serializer serializer)
    {
        this.serializer = new OrleansGrainStorageSerializer(serializer);
    }

    /// <inheritdoc />
    public T Deserialize<T>(BsonValue value)
    {
        return serializer.Deserialize<T>(value[BinaryElementName].AsByteArray)!;
    }

    /// <inheritdoc />
    public BsonValue Serialize<T>(T state)
    {
        var binaryData = serializer.Serialize(state);
        return new BsonDocument(BinaryElementName, new BsonBinaryData(binaryData.ToArray()));
    }
}
