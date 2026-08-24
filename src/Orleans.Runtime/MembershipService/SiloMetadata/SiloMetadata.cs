using System.Collections.Generic;
using System.Collections.Immutable;

namespace Orleans.Runtime.MembershipService.SiloMetadata;

/// <summary>
/// Represents metadata associated with a silo for the lifetime of that silo instance.
/// </summary>
/// <remarks>
/// Membership providers persist this data with the silo's membership entry. Metadata can become
/// available after an initial snapshot and is then retained for that silo instance. The configured
/// membership provider determines the supported storage size.
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

    /// <summary>
    /// Gets the metadata key-value pairs associated with the silo.
    /// </summary>
    [Id(0)]
    public ImmutableDictionary<string, string> Metadata { get; private set; } = ImmutableDictionary<string, string>.Empty;

    internal void AddMetadata(IEnumerable<KeyValuePair<string, string>> metadata) => Metadata = Metadata.SetItems(metadata);
    internal void AddMetadata(string key, string value) => Metadata = Metadata.SetItem(key, value);
}
