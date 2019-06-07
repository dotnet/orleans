using System;
using System.Collections.Immutable;
using Orleans.Runtime.MembershipService;

namespace Orleans.Runtime
{
    [Serializable]
    internal sealed class ClusterMembershipSnapshot
    {
        public ClusterMembershipSnapshot(ImmutableDictionary<SiloAddress, ClusterMember> members, MembershipVersion version)
        {
            this.Members = members;
            this.Version = version;
        }

        internal static ClusterMembershipSnapshot Create(MembershipTableSnapshot membership)
        {
            var memberBuilder = ImmutableDictionary.CreateBuilder<SiloAddress, ClusterMember>();
            foreach (var member in membership.Entries)
            {
                var entry = member.Value;
                memberBuilder[entry.SiloAddress] = new ClusterMember(entry.SiloAddress, entry.Status, entry.SiloName);
            }

            return new ClusterMembershipSnapshot(memberBuilder.ToImmutable(), membership.Version);
        }

        public ImmutableDictionary<SiloAddress, ClusterMember> Members { get; }

        public MembershipVersion Version { get; }

        public SiloStatus GetSiloStatus(SiloAddress silo)
        {
            var status = this.Members.TryGetValue(silo, out var entry) ? entry.Status : SiloStatus.None;
            if (status == SiloStatus.None)
            {
                foreach (var member in this.Members)
                {
                    if (member.Key.IsSuccessorOf(silo))
                    {
                        status = SiloStatus.Dead;
                        break;
                    }
                }
            }

            return status;
        }

        public ClusterMembershipUpdate CreateInitialUpdateNotification() => new ClusterMembershipUpdate(this, this.Members.Values.ToImmutableArray());

        public ClusterMembershipUpdate CreateUpdateNotification(ClusterMembershipSnapshot previous)
        {
            if (previous is null) throw new ArgumentNullException(nameof(previous));
            if (this.Version < previous.Version)
            {
                throw new ArgumentException($"Argument must have a previous version to the current instance. Expected <= {this.Version}, encountered {previous.Version}", nameof(previous));
            }

            if (this.Version == previous.Version)
            {
                return new ClusterMembershipUpdate(this, ImmutableArray<ClusterMember>.Empty);
            }

            var changes = ImmutableHashSet.CreateBuilder<ClusterMember>();
            foreach (var entry in this.Members)
            {
                // Include any entry which is new or has changed state.
                if (!previous.Members.TryGetValue(entry.Key, out var previousEntry) || previousEntry.Status != entry.Value.Status)
                {
                    changes.Add(entry.Value);
                }
            }

            // Handle entries which were removed entirely.
            foreach (var entry in previous.Members)
            {
                if (!this.Members.TryGetValue(entry.Key, out var newEntry))
                {
                    changes.Add(new ClusterMember(entry.Key, SiloStatus.Dead, entry.Value.Name));
                }
            }

            return new ClusterMembershipUpdate(this, changes.ToImmutableArray());
        }
    }
}
