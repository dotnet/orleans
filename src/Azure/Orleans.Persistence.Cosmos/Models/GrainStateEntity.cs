using Newtonsoft.Json;

namespace Orleans.Persistence.Cosmos;

internal class GrainStateEntity<TState> : BaseEntity
{
    [JsonProperty(nameof(GrainType))]
    [JsonPropertyName(nameof(GrainType))]
    public string GrainType { get; set; } = default!;

    [JsonProperty(nameof(State))]
    [JsonPropertyName(nameof(State))]
    public TState State { get; set; } = default!;

    [JsonProperty(nameof(PartitionKey))]
    [JsonPropertyName(nameof(PartitionKey))]
    public string PartitionKey { get; set; } = default!;
}