using System.Collections.Generic;
using System.Collections.Immutable;

namespace Orleans.Runtime.MembershipService.SiloMetadata;

/// <summary>
/// Describes the metadata associated with a silo.
/// </summary>
[GenerateSerializer]
[Alias("Orleans.Runtime.MembershipService.SiloMetadata.SiloMetadata")]
public record SiloMetadata
{
    /// <summary>
    /// Gets an instance which contains no metadata.
    /// </summary>
    public static SiloMetadata Empty { get; } = new SiloMetadata();

    /// <summary>
    /// Gets the metadata key-value pairs associated with the silo.
    /// </summary>
    [Id(0)]
    public ImmutableDictionary<string, string> Metadata { get; private set; } = ImmutableDictionary<string, string>.Empty;

    internal void AddMetadata(IEnumerable<KeyValuePair<string, string>> metadata) => Metadata = Metadata.SetItems(metadata);
    internal void AddMetadata(string key, string value) => Metadata = Metadata.SetItem(key, value);
}
