using Newtonsoft.Json;

namespace Orleans.Journaling.Cosmos;

internal sealed class CosmosLogEntry : BaseEntity
{
    /// <summary>
    /// Used for partitioning.
    /// </summary>
    [JsonProperty(nameof(LogId))]
    [JsonPropertyName(nameof(LogId))]
    public string LogId { get; set; } = "";

    /// <summary>
    /// Used for ordering.
    /// </summary>
    [JsonProperty(nameof(SequenceNumber))]
    [JsonPropertyName(nameof(SequenceNumber))]
    public long SequenceNumber { get; set; }

    /// <summary>
    /// Used for identification of the entry type.
    /// </summary>
    [JsonProperty(nameof(EntryType))]
    [JsonPropertyName(nameof(EntryType))]
    public CosmosLogEntryType EntryType { get; set; }

    /// <summary>
    /// The actual log data.
    /// </summary>
    [JsonProperty(nameof(Data))]
    [JsonPropertyName(nameof(Data))]
    public byte[] Data { get; set; } = [];
}
