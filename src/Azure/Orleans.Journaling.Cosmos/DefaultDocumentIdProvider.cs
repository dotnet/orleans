using static CosmosIdSanitizer;

namespace Orleans.Journaling.Cosmos;

internal sealed class DefaultDocumentIdProvider(IOptions<ClusterOptions> options) : IDocumentIdProvider
{
    private const string KEY_STRING_SEPARATOR = "__";
    private readonly ClusterOptions _options = options.Value;

    public (string DocumentId, PartitionKey PartitionKey) GetIdentifiers(GrainId grainId) =>
        (GetId(grainId), GetPartitionKey(grainId));

    public string GetId(GrainId grainId) =>
        $"{Sanitize(_options.ServiceId)}{KEY_STRING_SEPARATOR}" +
        $"{Sanitize(grainId.Type.ToString()!)}" +
        $"{SeparatorChar}{Sanitize(grainId.Key.ToString()!)}";

    public static PartitionKey GetPartitionKey(GrainId grainId) => new(Sanitize(grainId.Type.ToString()!));
}
