using static Orleans.Persistence.Cosmos.CosmosIdSanitizer;

namespace Orleans.Persistence.Cosmos;

/// <summary>
/// The default implementation of <see cref="IDocumentIdProvider"/>.
/// </summary>
public sealed class DefaultDocumentIdProvider : IDocumentIdProvider
{
    private const string KEY_STRING_SEPARATOR = "__";
    private readonly ClusterOptions _options;
#pragma warning disable CS0618 // Type or member is obsolete
    private readonly IPartitionKeyProvider? _partitionKeyProvider;
#pragma warning restore CS0618 // Type or member is obsolete
    internal bool HasCustomPartitionKeyProvider => _partitionKeyProvider is not null;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultDocumentIdProvider"/> class.
    /// </summary>
    /// <param name="options">The cluster options.</param>
    public DefaultDocumentIdProvider(IOptions<ClusterOptions> options)
    {
        _options = options.Value;
    }

#pragma warning disable CS0618 // Type or member is obsolete
    internal DefaultDocumentIdProvider(IOptions<ClusterOptions> options, IPartitionKeyProvider partitionKeyProvider)
        : this(options)
    {
        _partitionKeyProvider = partitionKeyProvider;
    }
#pragma warning restore CS0618 // Type or member is obsolete

    /// <inheritdoc/>
    public ValueTask<(string DocumentId, string PartitionKey)> GetDocumentIdentifiers(string grainType, GrainId grainId)
    {
        var documentId = GetId(grainType, grainId);
        if (_partitionKeyProvider is null)
        {
            return new((documentId, GetPartitionKey(grainType, grainId)));
        }

        return GetDocumentIdentifiers(documentId, grainType, grainId);
    }

    /// <summary>
    /// Gets the id for the specified grain document.
    /// </summary>
    /// <param name="grainType">The grain type.</param>
    /// <param name="grainId">The grain id.</param>
    /// <returns>The document id.</returns>
    public string GetId(string grainType, GrainId grainId) => $"{Sanitize(_options.ServiceId)}{KEY_STRING_SEPARATOR}{Sanitize(grainId.Type.ToString()!)}{SeparatorChar}{Sanitize(grainId.Key.ToString()!)}";

    /// <summary>
    /// Gets the Cosmos DB partition key for the specified grain document.
    /// </summary>
    /// <param name="grainType">The grain type.</param>
    /// <param name="grainId">The grain id.</param>
    /// <returns>The document partition key.</returns>
    public string GetPartitionKey(string grainType, GrainId grainId) => Sanitize(grainType);

    private async ValueTask<(string DocumentId, string PartitionKey)> GetDocumentIdentifiers(string documentId, string grainType, GrainId grainId)
    {
#pragma warning disable CS0618 // Type or member is obsolete
        var partitionKey = await _partitionKeyProvider!.GetPartitionKey(grainType, grainId);
#pragma warning restore CS0618 // Type or member is obsolete
        return (documentId, partitionKey);
    }
}