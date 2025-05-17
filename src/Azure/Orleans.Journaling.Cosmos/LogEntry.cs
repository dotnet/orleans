using Newtonsoft.Json;

namespace Orleans.Journaling;

internal class LogEntry : BaseEntity
{
    [JsonProperty(nameof(LogId))]
    [JsonPropertyName(nameof(LogId))]
    public string LogId { get; set; } = "";

    [JsonProperty(nameof(SequenceNumber))]
    [JsonPropertyName(nameof(SequenceNumber))]
    public long SequenceNumber { get; set; }

    [JsonProperty(nameof(EntryType))]
    [JsonPropertyName(nameof(EntryType))]
    public LogEntryType EntryType { get; set; }

    [JsonProperty(nameof(Data))]
    [JsonPropertyName(nameof(Data))]
    public byte[] Data { get; set; } = [];
}
