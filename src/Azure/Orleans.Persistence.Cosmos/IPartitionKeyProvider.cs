namespace Orleans.Persistence.Cosmos;

/// <summary>
/// Creates a partition key for the provided grain.
/// </summary>
public interface IPartitionKeyProvider
{
    /// <summary>
    /// Creates a partition key for the provided grain.
    /// </summary>
    /// <param name="grainType">The grain type.</param>
    /// <param name="grainId">The grain identifier.</param>
    /// <returns>The partition key.</returns>
    ValueTask<string> GetPartitionKey(string grainType, GrainId grainId);
}

internal class DefaultPartitionKeyProvider : IPartitionKeyProvider
{
    public ValueTask<string> GetPartitionKey(string grainType, GrainId grainId) => new(CosmosIdSanitizer.Sanitize(grainType));
}