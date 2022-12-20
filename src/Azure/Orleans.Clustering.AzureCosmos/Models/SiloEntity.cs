namespace Orleans.Clustering.AzureCosmos.Models;

internal class SiloEntity : BaseClusterEntity
{
    public override string EntityType => nameof(SiloEntity);

    [JsonPropertyName(nameof(Address))]
    public string Address { get; set; } = default!;

    [JsonPropertyName(nameof(Port))]
    public int Port { get; set; }

    [JsonPropertyName(nameof(Generation))]
    public int Generation { get; set; }

    [JsonPropertyName(nameof(Hostname))]
    public string Hostname { get; set; } = default!;

    [JsonPropertyName(nameof(Status))]
    public int Status { get; set; }

    [JsonPropertyName(nameof(ProxyPort))]
    public int? ProxyPort { get; set; }

    [JsonPropertyName(nameof(SiloName))]
    public string SiloName { get; set; } = default!;

    [JsonPropertyName(nameof(SuspectingSilos))]
    public List<string> SuspectingSilos { get; set; } = new();

    [JsonPropertyName(nameof(SuspectingTimes))]
    public List<string> SuspectingTimes { get; set; } = new();

    [JsonPropertyName(nameof(StartTime))]
    public DateTimeOffset StartTime { get; set; }

    [JsonPropertyName(nameof(IAmAliveTime))]
    public DateTimeOffset IAmAliveTime { get; set; }

}