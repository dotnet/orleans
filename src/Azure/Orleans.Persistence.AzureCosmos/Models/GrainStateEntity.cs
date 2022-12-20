namespace Orleans.Persistence.AzureCosmos;

internal class GrainStateEntity<TState> : BaseEntity
{
    [JsonPropertyName(nameof(GrainType))]
    public string GrainType { get; set; } = default!;

    [JsonPropertyName(nameof(State))]
    public TState State { get; set; } = default!;

    [JsonPropertyName(nameof(PartitionKey))]
    public string PartitionKey { get; set; } = default!;
}