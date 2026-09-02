using System.Collections.Generic;
using System.Collections.Immutable;

namespace Orleans.Runtime.MembershipService.SiloMetadata;

/// <summary>
/// Represents metadata associated with a silo for the lifetime of that silo instance.
/// </summary>
/// <remarks>
/// Membership providers persist this data with the silo's membership entry. Metadata can become
/// available after an initial snapshot and is then retained for that silo instance. The configured
/// membership provider determines the supported storage size. During a mixed-version rolling upgrade,
/// an older replace-style provider write can temporarily remove inline metadata. An active
/// metadata-aware silo restores its own metadata on its next heartbeat.
/// </remarks>
[GenerateSerializer]
[Alias("Orleans.Runtime.MembershipService.SiloMetadata.SiloMetadata")]
public record SiloMetadata
{
    /// <summary>
    /// Gets an available metadata value with no entries.
    /// </summary>
    public static SiloMetadata Empty { get; } = new SiloMetadata();

    /// <summary>
    /// Initializes a new instance of the <see cref="SiloMetadata"/> class.
    /// </summary>
    public SiloMetadata()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SiloMetadata"/> class.
    /// </summary>
    /// <param name="metadata">The metadata key-value pairs associated with the silo.</param>
    public SiloMetadata(IEnumerable<KeyValuePair<string, string>> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        AddMetadata(metadata);
    }

    [Id(0)]
    private ImmutableDictionary<string, string> MetadataStorage { get; set; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Gets the metadata key-value pairs associated with the silo.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata => MetadataStorage;

    internal void AddMetadata(IEnumerable<KeyValuePair<string, string>> metadata) => MetadataStorage = MetadataStorage.SetItems(metadata);
    internal void AddMetadata(string key, string value) => MetadataStorage = MetadataStorage.SetItem(key, value);
}
