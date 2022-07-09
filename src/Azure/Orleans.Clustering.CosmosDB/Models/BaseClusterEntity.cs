namespace Orleans.Clustering.CosmosDB;

internal abstract class BaseClusterEntity : BaseEntity
{
    [JsonPropertyName(nameof(ClusterId))]
    public string ClusterId { get; set; } = default!;

    [JsonPropertyName(nameof(EntityType))]
    public abstract string EntityType { get; }
}