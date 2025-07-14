using Orleans.Configuration;

namespace Orleans.Streaming.Migration.Configuration;

/// <summary>
/// Options for configuring the Azure Queue migration stream provider.
/// </summary>
public class AzureQueueMigrationOptions : AzureQueueOptions
{
    public SerializationMode SerializationMode { get; set; }
}

/// <summary>
/// Serialization mode used in the Migration Azure Queue stream provider.
/// </summary>
public enum SerializationMode
{
    /// <summary>
    /// Uses the 3.x payload serialization format by default.
    /// </summary>
    Default = 0,

    /// <summary>
    /// Uses the JSON format for payload serialization.
    /// </summary>
    Json = 1,

    /// <summary>
    /// Uses the JSON format for payload serialization, and if fails on read/writes it will try to read/write the payload in the 3.x format.
    /// </summary>
    PrioritizeJson = 2
}
