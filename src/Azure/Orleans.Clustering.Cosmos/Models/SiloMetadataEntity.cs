using Newtonsoft.Json;

namespace Orleans.Clustering.Cosmos.Models;

internal sealed class SiloMetadataEntity : BaseEntity
{
    [JsonProperty(nameof(ClusterId))]
    [JsonPropertyName(nameof(ClusterId))]
    public string ClusterId { get; set; } = default!;

    [JsonProperty(nameof(Metadata))]
    [JsonPropertyName(nameof(Metadata))]
    public Dictionary<string, string> Metadata { get; set; } = new();

    [JsonProperty(nameof(CreatedAt))]
    [JsonPropertyName(nameof(CreatedAt))]
    public DateTimeOffset CreatedAt { get; set; }
}
