using System.IO;
using System.Text.Json;
using Azure.Core.Serialization;

#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.CosmosDB;
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.CosmosDB;
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.CosmosDB;
#elif ORLEANS_STREAMING
namespace Orleans.Streaming.CosmosDB;
#elif ORLEANS_DIRECTORY
namespace Orleans.GrainDirectory.CosmosDB;
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif

// Custom serializar for Azure CosmosDB that uses System.Text.Json
internal class AzureCosmosDBSTJSerializer : CosmosSerializer
{
    private readonly JsonObjectSerializer _systemTextJsonSerializer;

    public AzureCosmosDBSTJSerializer(JsonSerializerOptions jsonSerializerOptions)
    {
        this._systemTextJsonSerializer = new JsonObjectSerializer(jsonSerializerOptions);
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
            return (T)this._systemTextJsonSerializer.Deserialize(stream, typeof(T), default)!;
        }
    }

    public override Stream ToStream<T>(T input)
    {
        var streamPayload = new MemoryStream();
        this._systemTextJsonSerializer.Serialize(streamPayload, input, typeof(T), default);
        streamPayload.Position = 0;
        return streamPayload;
    }
}