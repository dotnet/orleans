using System.Collections.Immutable;
using System.Threading.Tasks;
using Orleans.Metadata;
using Orleans.Runtime.Dissemination;

namespace Orleans.Runtime
{
    internal sealed class ClusterManifestSystemTarget : SystemTarget, IClusterManifestSystemTarget, ISiloManifestSystemTarget, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly GrainManifest _siloManifest;
        private readonly ManifestHash _siloManifestHash;
        private readonly IClusterMembershipService _clusterMembershipService;
        private readonly IClusterManifestProvider _clusterManifestProvider;
        private readonly ClusterManifestUpdate? _noUpdate = default;
        private MembershipVersion _cachedMembershipVersion;
        private ClusterManifestUpdate? _cachedUpdate;

        public ClusterManifestSystemTarget(
            IClusterMembershipService clusterMembershipService,
            IClusterManifestProvider clusterManifestProvider,
            SystemTargetShared shared)
            : base(Constants.ManifestProviderType, shared)
        {
            _siloManifest = clusterManifestProvider.LocalGrainManifest;
            _siloManifestHash = ManifestHashCalculator.ComputeHash(_siloManifest);
            _clusterMembershipService = clusterMembershipService;
            _clusterManifestProvider = clusterManifestProvider;
            shared.ActivationDirectory.RecordNewTarget(this);
        }

        public ValueTask<ClusterManifest> GetClusterManifest() => new(_clusterManifestProvider.Current);
        public ValueTask<ClusterManifestHashSummary> GetClusterManifestHashSummary()
        {
            var manifest = _clusterManifestProvider.Current;
            var hashes = ImmutableDictionary.CreateBuilder<SiloAddress, ManifestHash>();
            foreach (var siloManifest in manifest.Silos)
            {
                hashes[siloManifest.Key] = ManifestHashCalculator.ComputeHash(siloManifest.Value);
            }

            return new(new ClusterManifestHashSummary(manifest.Version, hashes.ToImmutable()));
        }

        public ValueTask<ManifestHash> GetSiloManifestHash() => new(_siloManifestHash);

        public ValueTask<GrainManifest> GetSiloManifestByHash(ManifestHash hash) =>
            new(hash == _siloManifestHash ? _siloManifest : null);

        public ValueTask<ClusterManifestUpdate?> GetClusterManifestUpdate(MajorMinorVersion version)
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
        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            // We don't participate in any lifecycle stages: activating this instance is all that is necessary.
        }
    }
}
