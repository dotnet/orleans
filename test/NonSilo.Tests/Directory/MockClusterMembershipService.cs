using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Utilities;

namespace UnitTests.Directory
{
    internal class MockClusterMembershipService : IClusterMembershipService
    {
        private long version = 0;
        private Dictionary<SiloAddress, SiloStatus> statuses;
        private ClusterMembershipSnapshot snapshot;
        private readonly AsyncEnumerable<ClusterMembershipSnapshot> updates;

        ClusterMembershipSnapshot IClusterMembershipService.CurrentSnapshot => this.snapshot;

        public MembershipVersion CurrentVersion => this.snapshot.Version;

        IAsyncEnumerable<ClusterMembershipSnapshot> IClusterMembershipService.MembershipUpdates => this.updates;

        public IClusterMembershipService Target => this;

        public MockClusterMembershipService(Dictionary<SiloAddress, SiloStatus> initialStatuses = null)
        {
            this.statuses = initialStatuses ?? new Dictionary<SiloAddress, SiloStatus>();
            this.snapshot = ToSnapshot(this.statuses, ++version);
            this.updates = this.updates = new AsyncEnumerable<ClusterMembershipSnapshot>(
                (previous, proposed) => proposed.Version == MembershipVersion.MinValue || proposed.Version > previous.Version,
                this.snapshot)
            {
                OnPublished = update => Interlocked.Exchange(ref this.snapshot, update)
            };
        }

        public void UpdateSiloStatus(SiloAddress siloAddress, SiloStatus siloStatus)
        {
            this.statuses[siloAddress] = siloStatus;
            this.updates.Publish(ToSnapshot(this.statuses, ++version));
        }

        internal static ClusterMembershipSnapshot ToSnapshot(Dictionary<SiloAddress, SiloStatus> statuses, long version)
        {
            var dictBuilder = ImmutableDictionary.CreateBuilder<SiloAddress, ClusterMember>();
            foreach (var kvp in statuses)
                dictBuilder.Add(kvp.Key, new ClusterMember(kvp.Key, kvp.Value));

            return new ClusterMembershipSnapshot(dictBuilder.ToImmutable(), new MembershipVersion(version));
        }

        public ValueTask Refresh(MembershipVersion minimumVersion = default) => new ValueTask();
    }
}
