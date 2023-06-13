namespace Orleans.Persistence.AzureCosmos;

public interface IPartitionKeyProvider
{
    ValueTask<string> GetPartitionKey(string grainType, GrainId grainId);
}

internal class DefaultPartitionKeyProvider : IPartitionKeyProvider
{
    public ValueTask<string> GetPartitionKey(string grainType, GrainId grainId) => new(CosmosDbIdSanitizer.Sanitize(grainType));
}