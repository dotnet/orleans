using static Orleans.Persistence.Cosmos.CosmosIdSanitizer;

namespace Orleans.Persistence.Cosmos.DocumentIdProviders;

/// <summary>
/// Base DocumentId provider using <see cref="ServiceId"/> to prefix the documentId (if supplied)
/// </summary>
public abstract class DocumentIdProviderBase : IDocumentIdProvider
{
    private const string KEY_STRING_SEPARATOR = "__";

    protected string? ServiceId { get; init; }

    public (string DocumentId, string PartitionKey) GetDocumentIdentifiers(string stateName, string grainTypeName, string grainKey)
    {
        _ = stateName ?? throw new ArgumentNullException(nameof(stateName));
        _ = grainTypeName ?? throw new ArgumentNullException(nameof(grainTypeName));
        _ = grainKey ?? throw new ArgumentNullException(nameof(grainKey));

        var partitionKey = Sanitize(stateName);
        var documentId = this.ServiceId == null ?
            $"{Sanitize(grainTypeName)}{SeparatorChar}{Sanitize(grainKey)}" :
            $"{Sanitize(this.ServiceId)}{KEY_STRING_SEPARATOR}{Sanitize(grainTypeName)}{SeparatorChar}{Sanitize(grainKey)}";

        return new(documentId, partitionKey);
    }
}
