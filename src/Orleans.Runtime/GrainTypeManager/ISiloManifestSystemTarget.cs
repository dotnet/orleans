using Orleans.Metadata;

namespace Orleans.Runtime
{
    internal interface ISiloManifestSystemTarget : ISystemTarget
    {
        ValueTask<GrainManifest> GetSiloManifest();
    }
}