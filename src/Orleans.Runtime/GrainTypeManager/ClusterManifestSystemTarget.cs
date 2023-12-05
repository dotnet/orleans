using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Metadata;

namespace Orleans.Runtime
{
    internal class ClusterManifestSystemTarget : SystemTarget, IClusterManifestSystemTarget, ISiloManifestSystemTarget
    {
        private readonly GrainManifest _siloManifest;
        private readonly IClusterMembershipService _clusterMembershipService;
        private readonly IClusterManifestProvider _clusterManifestProvider;
        private readonly ClusterManifestUpdate _noUpdate = default;
        private MembershipVersion _cachedMembershipVersion;
        private ClusterManifestUpdate _cachedUpdate;

        public ClusterManifestSystemTarget(
            IClusterMembershipService clusterMembershipService,
            IClusterManifestProvider clusterManifestProvider,
            ILocalSiloDetails siloDetails,
            ILoggerFactory loggerFactory)
            : base(Constants.ManifestProviderType, siloDetails.SiloAddress, loggerFactory)
        {
            _siloManifest = clusterManifestProvider.LocalGrainManifest;
            _clusterMembershipService = clusterMembershipService;
            _clusterManifestProvider = clusterManifestProvider;
        }

        public ValueTask<ClusterManifest> GetClusterManifest() => new(_clusterManifestProvider.Current);
        public ValueTask<ClusterManifestUpdate> GetClusterManifestUpdate(MajorMinorVersion version)
        {
            var manifest = _clusterManifestProvider.Current;

            // Only return an updated manifest if it is newer than the provided version.
            if (manifest.Version <= version)
            {
                return new (_noUpdate);
            }

            // Maintain a cache of whether the current manifest contains all active servers so that it
            // does not need to be recomputed each time.
            var membershipSnapshot = _clusterMembershipService.CurrentSnapshot;
            if (_cachedUpdate is null
                || membershipSnapshot.Version > _cachedMembershipVersion
                || manifest.Version > _cachedUpdate.Version)
            {
                var includesAllActiveServers = true;
                foreach (var server in membershipSnapshot.Members)
                {
                    if (server.Value.Status == SiloStatus.Active)
                    {
                        if (!manifest.Silos.ContainsKey(server.Key))
                        {
                            includesAllActiveServers = false;
                        }
                    }
                }

                _cachedUpdate = new ClusterManifestUpdate(manifest.Version, manifest.Silos, includesAllActiveServers);
                _cachedMembershipVersion = membershipSnapshot.Version;
            }

            return new (_cachedUpdate);
        }

        public ValueTask<GrainManifest> GetSiloManifest() => new(_siloManifest);
    }
}