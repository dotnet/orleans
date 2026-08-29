using Newtonsoft.Json;

namespace Orleans.Persistence.Cosmos;

internal class GrainStateEntity<TState> : BaseEntity
{
    [JsonProperty(nameof(GrainType))]
    [JsonPropertyName(nameof(GrainType))]
    public string GrainType { get; set; } = default!;

    [JsonProperty(nameof(State))]
    [JsonPropertyName(nameof(State))]
    public TState? State { get; set; }

    [JsonProperty(nameof(PartitionKey))]
    [JsonPropertyName(nameof(PartitionKey))]
    public string PartitionKey { get; set; } = default!;

    [JsonProperty(nameof(PartitionKey2), NullValueHandling = NullValueHandling.Ignore)]
    [JsonPropertyName(nameof(PartitionKey2))]
    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PartitionKey2 { get; set; }

    [JsonProperty(nameof(PartitionKey3), NullValueHandling = NullValueHandling.Ignore)]
    [JsonPropertyName(nameof(PartitionKey3))]
    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PartitionKey3 { get; set; }
}
