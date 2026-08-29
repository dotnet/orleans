namespace Orleans.Persistence.Cosmos;

/// <summary>
/// Identifies a Cosmos DB grain-state document and its ordered partition-key values.
/// </summary>
/// <param name="DocumentId">The document identifier.</param>
/// <param name="PartitionKeyValues">The partition-key values, in container partition-key path order.</param>
public readonly record struct CosmosDocumentKey(string DocumentId, IReadOnlyList<string> PartitionKeyValues);
