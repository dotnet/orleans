using System;
using System.Text.Json.Serialization;
using Newtonsoft.Json;
using Orleans.Reminders.Cosmos.Shared;
using Orleans.Runtime;

namespace Orleans.Reminders.Cosmos.Models;

internal abstract class BaseEntity
{
    internal const string ID_FIELD = "id";
    internal const string ETAG_FIELD = "_etag";

    [JsonProperty(ID_FIELD)]
    [JsonPropertyName(ID_FIELD)]
    public string Id { get; set; } = default!;

    [JsonProperty(ETAG_FIELD)]
    [JsonPropertyName(ETAG_FIELD)]
    public string ETag { get; set; } = default!;
}

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

    public static string ConstructId(GrainReference grainId, string reminderName)
    {
        var grainType = grainId.Type.ToString();
        var grainKey = grainId.Key.ToString();

        if (grainType is null || grainKey is null)
        {
            throw new ArgumentNullException(nameof(grainId));
        }

        return $"{CosmosIdSanitizer.Sanitize(grainType)}{CosmosIdSanitizer.SeparatorChar}{CosmosIdSanitizer.Sanitize(grainKey)}{CosmosIdSanitizer.SeparatorChar}{CosmosIdSanitizer.Sanitize(reminderName)}";
    }

    public static string ConstructPartitionKey(string serviceId, GrainReference grainId) => $"{serviceId}_{grainId.GetUniformHashCode():X}";
}
