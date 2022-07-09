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

internal abstract class BaseEntity
{
    internal const string ID_FIELD = "id";
    internal const string ETAG_FIELD = "_etag";    

    [JsonPropertyName(ID_FIELD)]
    public string Id { get; set; } = default!;

    [JsonPropertyName(ETAG_FIELD)]
    public string ETag { get; set; } = default!;
}