namespace Orleans.Persistence.Cosmos;

/// <summary>
/// Gets document and partition identifiers for grain state documents.
/// </summary>
public interface IDocumentIdProvider
{
    /// <summary>
    /// Gets the document identifier for the specified grain.
    /// </summary>
    /// <param name="grainType">The grain type.</param>
    /// <param name="grainId">The grain identifier.</param>
    /// <returns>The document id and partition key.</returns>
    ValueTask<(string DocumentId, string PartitionKey)> GetDocumentIdentifiers(string grainType, GrainId grainId);

    /// <summary>
    /// Gets the document identifier and ordered partition-key values for the specified grain.
    /// </summary>
    /// <param name="grainType">The grain type.</param>
    /// <param name="grainId">The grain identifier.</param>
    /// <returns>The document identifier and ordered partition-key values.</returns>
    async ValueTask<CosmosDocumentKey> GetDocumentKey(string grainType, GrainId grainId)
    {
        var (documentId, partitionKey) = await GetDocumentIdentifiers(grainType, grainId).ConfigureAwait(false);
        return new(documentId, new[] { partitionKey });
    }
}
