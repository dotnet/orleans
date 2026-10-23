using Orleans.Providers.Streams.Common;
using Orleans.Serialization.Configuration;

[assembly: TypeManifestProvider(typeof(Orleans.Hosting.StreamingTypeManifestProvider))]

namespace Orleans.Hosting;

internal sealed class StreamingTypeManifestProvider : TypeManifestProviderBase
{
    protected override void ConfigureInner(TypeManifestOptions options)
    {
        options.AddAllowedType(typeof(PersistentStreamProvider));
    }
}
