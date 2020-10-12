using System.Threading.Tasks;
using Orleans.Metadata;

namespace Orleans.Runtime
{
    internal interface IClusterManifestSystemTarget : ISystemTarget
    {
        ValueTask<ClusterManifest> GetClusterManifest();
    }
}
