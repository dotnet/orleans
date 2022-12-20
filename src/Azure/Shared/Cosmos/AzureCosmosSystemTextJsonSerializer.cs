using System.IO;
using System.Text.Json;
using Azure.Core.Serialization;

#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.AzureCosmos;
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.AzureCosmos;
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.AzureCosmos;
#elif ORLEANS_STREAMING
namespace Orleans.Streaming.AzureCosmos;
#elif ORLEANS_DIRECTORY
namespace Orleans.GrainDirectory.AzureCosmos;
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif

// Custom serializer for Azure Cosmos DB that uses System.Text.Json
internal class AzureCosmosSystemTextJsonSerializer : CosmosSerializer
{
    private readonly JsonObjectSerializer _systemTextJsonSerializer;

    public AzureCosmosSystemTextJsonSerializer(JsonSerializerOptions jsonSerializerOptions)
    {
        _systemTextJsonSerializer = new JsonObjectSerializer(jsonSerializerOptions);
    }

    public override T FromStream<T>(Stream stream)
    {
        if (stream.CanSeek && stream.Length == 0)
        {
            return default!;
        }

        if (typeof(Stream).IsAssignableFrom(typeof(T)))
        {
            return (T)(object)stream;
        }

        using (stream)
        {
            return (T)_systemTextJsonSerializer.Deserialize(stream, typeof(T), default)!;
        }
    }

    public override Stream ToStream<T>(T input)
    {
        var streamPayload = new MemoryStream();
        _systemTextJsonSerializer.Serialize(streamPayload, input, typeof(T), default);
        streamPayload.Position = 0;
        return streamPayload;
    }
}