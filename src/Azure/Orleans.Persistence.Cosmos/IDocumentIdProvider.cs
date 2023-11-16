namespace Orleans.Persistence.Cosmos;

/// <summary>
/// Gets document and partition identifiers for grain state documents.
/// </summary>
public interface IDocumentIdProvider
{
    /// <summary>
    /// Gets the document identifier for the specified grain.
    /// </summary>
    /// <param name="stateName">The grain state name.</param>
    /// <param name="grainId">The grain identifier.</param>
    /// <returns>The document id and partition key.</returns>
    ValueTask<(string DocumentId, string PartitionKey)> GetDocumentIdentifiers(string stateName, GrainId grainId);
}
