using Newtonsoft.Json;

namespace Orleans.Clustering.Cosmos.Models;

internal class SiloEntity : BaseClusterEntity
{
    public override string EntityType => nameof(SiloEntity);

    [JsonProperty(nameof(Address))]
    [JsonPropertyName(nameof(Address))]
    public string Address { get; set; } = default!;

    [JsonProperty(nameof(Port))]
    [JsonPropertyName(nameof(Port))]
    public int Port { get; set; }

    [JsonProperty(nameof(Generation))]
    [JsonPropertyName(nameof(Generation))]
    public int Generation { get; set; }

    [JsonProperty(nameof(Hostname))]
    [JsonPropertyName(nameof(Hostname))]
    public string Hostname { get; set; } = default!;

    [JsonProperty(nameof(Status))]
    [JsonPropertyName(nameof(Status))]
    public int Status { get; set; }

    [JsonProperty(nameof(ProxyPort))]
    [JsonPropertyName(nameof(ProxyPort))]
    public int? ProxyPort { get; set; }

    [JsonProperty(nameof(SiloName))]
    [JsonPropertyName(nameof(SiloName))]
    public string SiloName { get; set; } = default!;

    [JsonProperty(nameof(SuspectingSilos))]
    [JsonPropertyName(nameof(SuspectingSilos))]
    public List<string> SuspectingSilos { get; set; } = new();

    [JsonProperty(nameof(SuspectingTimes))]
    [JsonPropertyName(nameof(SuspectingTimes))]
    public List<string> SuspectingTimes { get; set; } = new();

    [JsonProperty(nameof(StartTime))]
    [JsonPropertyName(nameof(StartTime))]
    public DateTimeOffset StartTime { get; set; }

    [JsonProperty(nameof(IAmAliveTime))]
    [JsonPropertyName(nameof(IAmAliveTime))]
    public DateTimeOffset IAmAliveTime { get; set; }

}