using static Orleans.Persistence.Cosmos.CosmosIdSanitizer;

namespace Orleans.Persistence.Cosmos;

/// <summary>
/// The default implementation of <see cref="IDocumentIdProvider"/>.
/// </summary>
public sealed class DefaultDocumentIdProvider : IDocumentIdProvider
{
    private const string KEY_STRING_SEPARATOR = "__";
    private readonly ClusterOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultDocumentIdProvider"/> class.
    /// </summary>
    /// <param name="options">The cluster options.</param>
    public DefaultDocumentIdProvider(IOptions<ClusterOptions> options)
    {
        _options = options.Value;
    }

    /// <inheritdoc/>
    public ValueTask<(string DocumentId, string PartitionKey)> GetDocumentIdentifiers(string stateName, GrainId grainId) => new((GetId(stateName, grainId), GetPartitionKey(stateName, grainId)));

    /// <summary>
    /// Gets the id for the specified grain state document.
    /// </summary>
    /// <param name="stateName">The state name.</param>
    /// <param name="grainId">The grain id.</param>
    /// <returns>The document id.</returns>
    public string GetId(string stateName, GrainId grainId) => $"{Sanitize(_options.ServiceId)}{KEY_STRING_SEPARATOR}{Sanitize(grainId.Type.ToString()!)}{SeparatorChar}{Sanitize(grainId.Key.ToString()!)}";

    /// <summary>
    /// Gets the Cosmos DB partition key for the specified grain state document.
    /// </summary>
    /// <param name="stateName">The state name.</param>
    /// <param name="grainId">The grain id.</param>
    /// <returns>The document partition key.</returns>
    public string GetPartitionKey(string stateName, GrainId grainId) => Sanitize(stateName);
}