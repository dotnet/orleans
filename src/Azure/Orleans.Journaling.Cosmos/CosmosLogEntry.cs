using Newtonsoft.Json;

namespace Orleans.Journaling.Cosmos;

internal enum LogEntryType
{
    /// <summary>
    /// This <strong>is</strong> a log entry.
    /// </summary>
    Log = 1,
    /// <summary>
    /// This represents a compacted entry.
    /// </summary>
    Compacted = 2,
    /// <summary>
    /// This represents an entry that compaction is pending.
    /// </summary>
    CompactionPending = 3
}


/// <summary>
/// Used for reading just the 'id' field of any given log entry document.
/// </summary>
/// <param name="value">The value of <see cref="BaseEntity.Id"/></param>
[method: Newtonsoft.Json.JsonConstructor]
[method: System.Text.Json.Serialization.JsonConstructor]
internal readonly struct LogEntryId(string value)
{
    [JsonProperty("id")]
    [JsonPropertyName("id")]
    public string Value { get; } = value;
}

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
    public LogEntryType EntryType { get; set; }

    /// <summary>
    /// The actual log data.
    /// </summary>
    [JsonProperty(nameof(Data))]
    [JsonPropertyName(nameof(Data))]
    public byte[] Data { get; set; } = [];
}
