using Newtonsoft.Json;
using static Orleans.Reminders.Cosmos.CosmosIdSanitizer;

namespace Orleans.Reminders.Cosmos.Models;

internal class ReminderEntity : BaseEntity
{
    [JsonProperty(nameof(PartitionKey))]
    [JsonPropertyName(nameof(PartitionKey))]
    public string PartitionKey { get; set; } = default!;

    [JsonProperty(nameof(ServiceId))]
    [JsonPropertyName(nameof(ServiceId))]
    public string ServiceId { get; set; } = default!;

    [JsonProperty(nameof(GrainId))]
    [JsonPropertyName(nameof(GrainId))]
    public string GrainId { get; set; } = default!;

    [JsonProperty(nameof(Name))]
    [JsonPropertyName(nameof(Name))]
    public string Name { get; set; } = default!;

    [JsonProperty(nameof(StartAt))]
    [JsonPropertyName(nameof(StartAt))]
    public DateTimeOffset StartAt { get; set; }

    [JsonProperty(nameof(Period))]
    [JsonPropertyName(nameof(Period))]
    public TimeSpan Period { get; set; }

    [JsonProperty(nameof(GrainHash))]
    [JsonPropertyName(nameof(GrainHash))]
    public uint GrainHash { get; set; }

    public static string ConstructId(GrainId grainId, string reminderName)
    {
        var grainType = grainId.Type.ToString();
        var grainKey = grainId.Key.ToString();

        if (grainType is null || grainKey is null)
        {
            throw new ArgumentNullException(nameof(grainId));
        }

        return $"{Sanitize(grainType)}{SeparatorChar}{Sanitize(grainKey)}{SeparatorChar}{Sanitize(reminderName)}";
    }

    public static string ConstructPartitionKey(string serviceId, GrainId grainId) => $"{serviceId}_{grainId.GetUniformHashCode():X}";
}
