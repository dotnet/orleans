using Orleans.Runtime;
using Orleans.Serialization.Configuration;

[assembly: TypeManifestProvider(typeof(Orleans.Serialization.OrleansTypeManifestProvider))]

namespace Orleans.Serialization;

internal sealed class OrleansTypeManifestProvider : TypeManifestProviderBase
{
    protected override void ConfigureInner(TypeManifestOptions options)
    {
        options.AddAllowedType(typeof(PlacementStrategy));
    }
}
