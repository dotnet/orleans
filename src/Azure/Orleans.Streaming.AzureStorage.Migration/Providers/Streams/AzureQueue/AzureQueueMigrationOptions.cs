using Orleans.Configuration;

namespace Orleans.Streaming.Migration.Configuration;

/// <summary>
/// Options for configuring the Azure Queue migration stream provider.
/// </summary>
public class AzureQueueMigrationOptions : AzureQueueOptions
{
    /// <summary>
    /// Gets or sets the format used to serialize queue messages.
    /// </summary>
    public SerializationMode SerializationMode { get; set; } = SerializationMode.Binary;

    /// <summary>
    /// Gets or sets the preferred format used to deserialize queue messages.
    /// </summary>
    public DeserializationMode DeserializationMode { get; set; } = DeserializationMode.PreferBinary;
}

/// <summary>
/// Specifies the serialization format used by the Azure Queue migration stream provider.
/// </summary>
public enum SerializationMode
{
    /// <summary>
    /// Uses the Orleans 3.x binary payload format.
    /// </summary>
    Binary = 0,

    /// <summary>
    /// Uses the JSON format for payload serialization.
    /// </summary>
    Json = 1,

    /// <summary>
    /// Uses JSON serialization and falls back to the Orleans 3.x binary format if serialization fails.
    /// </summary>
    JsonWithFallback = 2
}

/// <summary>
/// Specifies the preferred deserialization format used by the Azure Queue migration stream provider.
/// </summary>
public enum DeserializationMode
{
    /// <summary>
    /// Attempts binary deserialization first and falls back to JSON.
    /// </summary>
    PreferBinary = 0,

    /// <summary>
    /// Attempts JSON deserialization first and falls back to the Orleans 3.x binary format.
    /// </summary>
    PreferJson = 1
}