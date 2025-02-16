using static Orleans.Persistence.Cosmos.CosmosIdSanitizer;

namespace Orleans.Persistence.Cosmos.DocumentIdProviders;

/// <summary>
/// Base DocumentId provider using <see cref="serviceId"/> to prefix the documentId (if supplied)
/// </summary>
public abstract class DocumentIdProviderBase : IDocumentIdProvider
{
    private const string KEY_STRING_SEPARATOR = "__";
    protected string? serviceId;

    public (string documentId, string partitionKey) GetDocumentIdentifiers(string stateName, string grainTypeName, string grainKey)
    {
        _ = stateName ?? throw new ArgumentNullException(nameof(stateName));
        _ = grainTypeName ?? throw new ArgumentNullException(nameof(grainTypeName));
        _ = grainKey ?? throw new ArgumentNullException(nameof(grainKey));

        var partitionKey = Sanitize(stateName);
        var documentId = this.serviceId == null ?
            $"{Sanitize(grainTypeName)}{SeparatorChar}{Sanitize(grainKey)}" :
            $"{Sanitize(this.serviceId)}{KEY_STRING_SEPARATOR}{Sanitize(grainTypeName)}{SeparatorChar}{Sanitize(grainKey)}";

        return new(documentId, partitionKey);
    }
}
