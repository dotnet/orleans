namespace Orleans.Journaling.Cosmos;

/// <summary>
/// Gets document and partition identifiers for grain state documents.
/// </summary>
public interface IDocumentIdProvider
{
    /// <summary>
    /// Gets the document identifier for the specified grain.
    /// </summary>
    /// <param name="grainId">The grain identifier.</param>
    /// <returns>The document id and partition key.</returns>
    (string DocumentId, PartitionKey PartitionKey) GetIdentifiers(GrainId grainId);
}
