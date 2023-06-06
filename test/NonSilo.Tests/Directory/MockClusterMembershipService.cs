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

        ClusterMembershipSnapshot IClusterMembershipService.CurrentSnapshot => snapshot;

        public MembershipVersion CurrentVersion => snapshot.Version;

        IAsyncEnumerable<ClusterMembershipSnapshot> IClusterMembershipService.MembershipUpdates => updates;

        public IClusterMembershipService Target => this;

        public MockClusterMembershipService(Dictionary<SiloAddress, (SiloStatus Status, string Name)> initialStatuses = null)
        {
            statuses = initialStatuses ?? new Dictionary<SiloAddress, (SiloStatus Status, string Name)>();
            snapshot = ToSnapshot(statuses, ++version);
            updates = updates = new AsyncEnumerable<ClusterMembershipSnapshot>(
                (previous, proposed) => proposed.Version == MembershipVersion.MinValue || proposed.Version > previous.Version,
                snapshot)
            {
                OnPublished = update => Interlocked.Exchange(ref snapshot, update)
            };
        }

        public void UpdateSiloStatus(SiloAddress siloAddress, SiloStatus siloStatus, string name)
        {
            statuses[siloAddress] = (siloStatus, name);
            updates.Publish(ToSnapshot(statuses, ++version));
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
