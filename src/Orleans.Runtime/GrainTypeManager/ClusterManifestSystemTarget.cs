using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Metadata;

namespace Orleans.Runtime
{
    internal class ClusterManifestSystemTarget : SystemTarget, IClusterManifestSystemTarget, ISiloManifestSystemTarget
    {
        private readonly GrainManifest _siloManifest;
        private readonly IClusterManifestProvider _clusterManifestProvider;

        public ClusterManifestSystemTarget(
            IClusterManifestProvider clusterManifestProvider,
            ILocalSiloDetails siloDetails,
            ILoggerFactory loggerFactory)
            : base(Constants.ManifestProviderType, siloDetails.SiloAddress, loggerFactory)
        {
            _siloManifest = clusterManifestProvider.LocalGrainManifest;
            _clusterManifestProvider = clusterManifestProvider;
        }

        public ValueTask<GetClusterManifestResult> GetClusterManifest(MajorMinorVersion version)
        {
            return new ValueTask<GetClusterManifestResult>(_clusterManifestProvider.GetCurrent(version));
        } 

        public ValueTask<GrainManifest> GetSiloManifest() => new ValueTask<GrainManifest>(_siloManifest);
    }

   
}