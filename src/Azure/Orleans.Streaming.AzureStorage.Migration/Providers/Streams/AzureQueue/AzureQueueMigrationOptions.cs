using Orleans.Configuration;

namespace Orleans.Streaming.Migration.Configuration;

/// <summary>
/// Options for configuring the Azure Queue migration stream provider.
/// </summary>
public class AzureQueueMigrationOptions : AzureQueueOptions
{
    public SerializationMode SerializationMode { get; set; } = SerializationMode.Binary;

    public DeserializationMode DeserializationMode { get; set; } = DeserializationMode.PreferBinary;
}

/// <summary>
/// Serialization mode used in the Migration Azure Queue stream provider.
/// </summary>
public enum SerializationMode
{
    /// <summary>
    /// Uses the 3.x payload serialization format by default.
    /// </summary>
    Binary = 0,

    /// <summary>
    /// Uses the JSON format for payload serialization.
    /// </summary>
    Json = 1,

    /// <summary>
    /// Uses the JSON format for payload serialization, and if fails on read/writes it will try to read/write the payload in the binary format.
    /// </summary>
    JsonWithFallback = 2
}

/// <summary>
/// Deserialization mode used in the Migration Azure Queue stream provider.
/// It will also have a fallback to other format than preferred.
/// </summary>
public enum DeserializationMode
{
    /// <summary>
    /// Firstly deserialization happens via binary format and fallbacks to the JSON format if fails.
    /// </summary>
    PreferBinary = 0,

    /// <summary>
    /// Firstly deserialization happens via JSON format and fallbacks to the binary format if fails.
    /// </summary>
    PreferJson = 1
}