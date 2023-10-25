using System.Collections.Immutable;
using Orleans.Runtime;
using Orleans.Runtime.Utilities;

namespace UnitTests.Directory
{
    internal class MockClusterMembershipService : IClusterMembershipService
    {
        private long version = 0;
        private readonly Dictionary<SiloAddress, (SiloStatus Status, string Name)> statuses;
        private ClusterMembershipSnapshot snapshot;
        private readonly AsyncEnumerable<ClusterMembershipSnapshot> updates;

        ClusterMembershipSnapshot IClusterMembershipService.CurrentSnapshot => this.snapshot;

        public MembershipVersion CurrentVersion => this.snapshot.Version;

        IAsyncEnumerable<ClusterMembershipSnapshot> IClusterMembershipService.MembershipUpdates => this.updates;

        public IClusterMembershipService Target => this;

        public MockClusterMembershipService(Dictionary<SiloAddress, (SiloStatus Status, string Name)> initialStatuses = null)
        {
            this.statuses = initialStatuses ?? new Dictionary<SiloAddress, (SiloStatus Status, string Name)>();
            this.snapshot = ToSnapshot(this.statuses, ++version);
            this.updates = new AsyncEnumerable<ClusterMembershipSnapshot>(
                initialValue: this.snapshot,
                updateValidator: (previous, proposed) => proposed.Version > previous.Version,
                onPublished: update => Interlocked.Exchange(ref this.snapshot, update));
        }

        public void UpdateSiloStatus(SiloAddress siloAddress, SiloStatus siloStatus, string name)
        {
            this.statuses[siloAddress] = (siloStatus, name);
            this.updates.Publish(ToSnapshot(this.statuses, ++version));
        }

        internal static ClusterMembershipSnapshot ToSnapshot(Dictionary<SiloAddress, (SiloStatus Status, string Name)> statuses, long version)
        {
            var dictBuilder = ImmutableDictionary.CreateBuilder<SiloAddress, ClusterMember>();
            foreach (var kvp in statuses)
                dictBuilder.Add(kvp.Key, new ClusterMember(kvp.Key, kvp.Value.Status, kvp.Value.Name));

            return new ClusterMembershipSnapshot(dictBuilder.ToImmutable(), new MembershipVersion(version));
        }

        public ValueTask Refresh(MembershipVersion minimumVersion = default) => new ValueTask();

        public Task<bool> TryKill(SiloAddress siloAddress) => throw new NotImplementedException();
    }
}
