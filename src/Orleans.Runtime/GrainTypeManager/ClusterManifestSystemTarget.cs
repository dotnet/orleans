using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Metadata;

namespace Orleans.Runtime
{
    internal class ClusterManifestSystemTarget : SystemTarget, IClusterManifestSystemTarget, ISiloManifestSystemTarget
    {
        private readonly SiloManifest _siloManifest;
        private readonly IClusterManifestProvider _clusterManifestProvider;

        public ClusterManifestSystemTarget(
            SiloManifest siloManifest,
            IClusterManifestProvider clusterManifestProvider,
            ILocalSiloDetails siloDetails,
            ILoggerFactory loggerFactory)
            : base(Constants.ManifestProviderType, siloDetails.SiloAddress, loggerFactory)
        {
            _siloManifest = siloManifest;
            _clusterManifestProvider = clusterManifestProvider;
        }

        public ValueTask<ClusterManifest> GetClusterManifest() => new ValueTask<ClusterManifest>(_clusterManifestProvider.Current);

        public ValueTask<SiloManifest> GetSiloManifest() => new ValueTask<SiloManifest>(_siloManifest);
    }
}