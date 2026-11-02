using System;
using MongoDB.Bson.Serialization;

namespace Orleans.Providers.MongoDB.StorageProviders.Serializers.BsonSerializationProviders;

internal class OrleansBsonSerializationProvider : IBsonSerializationProvider
{
    public IBsonSerializer GetSerializer(Type type)
    {
        if (type.FullName == "Orleans.Streams.PubSubGrainState")
        {
            throw new NotImplementedException($"{nameof(BsonGrainStateSerializer)} does not support {ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME} storage provider, use {nameof(BinaryGrainStateSerializer)} or {nameof(JsonGrainStateSerializer)} instead.");
        }

        return default!;
    }
}
