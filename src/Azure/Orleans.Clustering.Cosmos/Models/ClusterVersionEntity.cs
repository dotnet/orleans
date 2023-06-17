using Newtonsoft.Json;

namespace Orleans.Clustering.Cosmos;

internal class ClusterVersionEntity : BaseClusterEntity
{
    public override string EntityType => nameof(ClusterVersionEntity);

    [JsonProperty(nameof(ClusterVersion))]
    [JsonPropertyName(nameof(ClusterVersion))]
    public int ClusterVersion { get; set; } = 0;
}