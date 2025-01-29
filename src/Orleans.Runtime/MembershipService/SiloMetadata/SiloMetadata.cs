using System.Collections.Generic;
using System.Collections.Immutable;

#nullable enable
namespace Orleans.Runtime.MembershipService.SiloMetadata;

[GenerateSerializer]
[Alias("Orleans.Runtime.MembershipService.SiloMetadata.SiloMetadata")]
public record SiloMetadata
{
    public static SiloMetadata Empty { get; } = new SiloMetadata();

    [Id(0)]
    public ImmutableDictionary<string, string> Metadata { get; private set; } = ImmutableDictionary<string, string>.Empty;

    internal void AddMetadata(IEnumerable<KeyValuePair<string, string>> metadata) => Metadata = Metadata.SetItems(metadata);
    internal void AddMetadata(string key, string value) => Metadata = Metadata.SetItem(key, value);
}