using static CosmosIdSanitizer;

namespace Orleans.Journaling.Cosmos;

/// <summary>
/// The default implementation of <see cref="IDocumentIdProvider"/>.
/// </summary>
internal sealed class DefaultDocumentIdProvider(IOptions<ClusterOptions> options) : IDocumentIdProvider
{
    private const string KEY_STRING_SEPARATOR = "__";
    private readonly ClusterOptions _options = options.Value;

    /// <inheritdoc/>
    public (string DocumentId, PartitionKey PartitionKey) GetIdentifiers(GrainId grainId) =>
        (GetId(grainId), GetPartitionKey(grainId));

    /// <summary>
    /// Gets the id for the specified grain state document.
    /// </summary>
    /// <param name="grainId">The grain id.</param>
    /// <returns>The document id.</returns>
    public string GetId(GrainId grainId) =>
        $"{Sanitize(_options.ServiceId)}{KEY_STRING_SEPARATOR}" +
        $"{Sanitize(grainId.Type.ToString()!)}" +
        $"{SeparatorChar}{Sanitize(grainId.Key.ToString()!)}";

    /// <summary>
    /// Gets the Cosmos DB partition key for the specified grain state document.
    /// </summary>
    /// <param name="grainId">The grain id.</param>
    /// <returns>The document partition key.</returns>
    public static PartitionKey GetPartitionKey(GrainId grainId) => new(Sanitize(grainId.Type.ToString()!));
}
