using System.Threading;
using System.Threading.Tasks;
using Orleans.Metadata;

namespace Orleans.Runtime
{
    internal interface ISiloManifestSystemTarget : ISystemTarget
    {
        [Alias("1857A4C8")]
        ValueTask<GrainManifest> GetSiloManifest(CancellationToken cancellationToken = default);
    }
}