namespace Orleans.Persistence.Cosmos;

/// <summary>
/// Creates a partition key for the provided grain.
/// </summary>
[Obsolete("Use IDocumentIdProvider instead.")]
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

#pragma warning disable CS0618 // Type or member is obsolete
internal sealed class DefaultPartitionKeyProvider : IPartitionKeyProvider
{
    public ValueTask<string> GetPartitionKey(string grainType, GrainId grainId) => new(CosmosIdSanitizer.Sanitize(grainType));
}
#pragma warning restore CS0618 // Type or member is obsolete
