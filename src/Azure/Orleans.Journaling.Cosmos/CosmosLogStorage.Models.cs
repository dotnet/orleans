using Newtonsoft.Json;

namespace Orleans.Journaling.Cosmos;

internal partial class CosmosLogStorage
{
    private class CosmosLogEntry : BaseEntity
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

    private enum LogEntryType
    {
        /// <summary>
        /// This is a log entry. Potentially a snapshot (after compaction).
        /// </summary>
        Log = 1,
        /// <summary>
        /// This represents a compacted entry.
        /// </summary>
        Compacted = 2,
        /// <summary>
        /// This is an entry which represents a pending compaction.
        /// </summary>
        CompactionPending = 3
    }

    /// <summary>
    /// Used for reading just the 'id' field of any given log entry document.
    /// </summary>
    /// <param name="value">The value of <see cref="BaseEntity.Id"/></param>
    [method: Newtonsoft.Json.JsonConstructor]
    [method: System.Text.Json.Serialization.JsonConstructor]
    private readonly struct LogEntryId(string value)
    {
        [JsonProperty("id")]
        [JsonPropertyName("id")]
        public string Value { get; } = value;
    }

    /// <summary>
    /// Used to build the in-memory state during initialization.
    /// </summary>
    private class LogSummary
    {
        [JsonProperty(nameof(EntriesCount))]
        [JsonPropertyName(nameof(EntriesCount))]
        public int EntriesCount { get; set; }

        /// <remarks>Nullable because MAX on an empty set is null.</remarks>
        [JsonProperty(nameof(MaxSequenceNumber))]
        [JsonPropertyName(nameof(MaxSequenceNumber))]
        public long? MaxSequenceNumber { get; set; }
    }
}
