namespace Orleans.Reminders.CosmosDB.Models;

internal class ReminderEntity : BaseEntity
{
    [JsonPropertyName(nameof(PartitionKey))]
    public string PartitionKey { get; set; } = default!;

    [JsonPropertyName(nameof(ServiceId))]
    public string ServiceId { get; set; } = default!;

    [JsonPropertyName(nameof(GrainId))]
    public string GrainId { get; set; } = default!;

    [JsonPropertyName(nameof(Name))]
    public string Name { get; set; } = default!;

    [JsonPropertyName(nameof(StartAt))]
    public DateTimeOffset StartAt { get; set; }

    [JsonPropertyName(nameof(Period))]
    public TimeSpan Period { get; set; }

    [JsonPropertyName(nameof(GrainHash))]
    public uint GrainHash { get; set; }

    public static string ConstructId(GrainId grainId, string reminderName) => $"{System.Net.WebUtility.UrlEncode(grainId.ToString())}-{reminderName}";

    public static string ConstructPartitionKey(string serviceId, GrainId grainId) => ConstructPartitionKey(serviceId, grainId.GetUniformHashCode());

    // IMPORTANT NOTE: Other code using this return data is very sensitive to format changes,
    //       so take great care when making any changes here!!!

    // this format of partition key makes sure that the comparisons in FindReminderEntries(begin, end) work correctly
    // the idea is that when converting to string, negative numbers start with 0, and positive start with 1. Now,
    // when comparisons will be done on strings, this will ensure that positive numbers are always greater than negative
    // string grainHash = number < 0 ? string.Format("0{0}", number.ToString("X")) : string.Format("1{0:d16}", number);
    public static string ConstructPartitionKey(string serviceId, uint number) => $"{serviceId}_{number:X8}";
}