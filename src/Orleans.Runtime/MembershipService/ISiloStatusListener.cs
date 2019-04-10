using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal sealed class ChangeFeedSource<T>
    {
        private ChangeFeedNode current;

        public ChangeFeedSource()
        {
            this.current = ChangeFeedNode.CreateInitial();
        }

        public ChangeFeedEntry<T> Current => this.current;

        public bool TryPublish(T value)
        {
            var newItem = new ChangeFeedNode(value);
            if (this.current.TrySetNext(newItem))
            {
                Interlocked.Exchange(ref this.current, newItem);
                return true;
            }

            return false;
        }

        private sealed class ChangeFeedNode : ChangeFeedEntry<T>
        {
            private readonly TaskCompletionSource<ChangeFeedEntry<T>> next;
            private readonly object value;

            public ChangeFeedNode(T value)
            {
                this.value = value;
                this.next = CreateCompletion();
            }

            public static ChangeFeedNode CreateInitial() => new ChangeFeedNode();

            private ChangeFeedNode()
            {
                this.value = ChangeFeed.InvalidValue;
                this.next = CreateCompletion();
            }

            public override bool HasValue => !ReferenceEquals(this.value, ChangeFeed.InvalidValue);

            public override T Value
            {
                get
                {
                    if (!this.HasValue) ThrowInvalidInstance();
                    if (this.value is T typedValue) return typedValue;
                    return default;
                }
            }

            public override Task<ChangeFeedEntry<T>> NextAsync() => this.next.Task;

            public bool TrySetNext(ChangeFeedNode next) => this.next.TrySetResult(next);

            private static T ThrowInvalidInstance() => throw new InvalidOperationException("This instance does not have a value set.");

            private static TaskCompletionSource<ChangeFeedEntry<T>> CreateCompletion()
                => new TaskCompletionSource<ChangeFeedEntry<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    internal static class ChangeFeed
    {
        internal static readonly object InvalidValue = new object();
    }

    public abstract class ChangeFeedEntry<T>
    {
        public abstract bool HasValue { get; }

        public abstract T Value { get; }

        public abstract Task<ChangeFeedEntry<T>> NextAsync();
    }

    /// <summary>
    /// Interface for types which listen to silo status change notifications.
    /// </summary>
    /// <remarks>
    /// To be implemented by different in-silo runtime components that are interested in silo status notifications from ISiloStatusOracle.
    /// </remarks>
    public interface ISiloStatusListener
    {
        /// <summary>
        /// Receive notifications about silo status events. 
        /// </summary>
        /// <param name="updatedSilo">A silo to update about.</param>
        /// <param name="status">The status of a silo.</param>
        void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status);
    }

    [Serializable]
    public struct MembershipVersion : IComparable<MembershipVersion>, IEquatable<MembershipVersion>
    {
        private readonly long version;

        public MembershipVersion(long version)
        {
            this.version = version;
        }

        public int CompareTo(MembershipVersion other) => this.version.CompareTo(other.version);

        public bool Equals(MembershipVersion other) => this.version == other.version;

        public override bool Equals(object obj) => obj is MembershipVersion other && this.Equals(other);

        public override int GetHashCode() => this.version.GetHashCode();

        public override string ToString() => this.version.ToString();

        public static bool operator ==(MembershipVersion left, MembershipVersion right) => left.version == right.version;
        public static bool operator !=(MembershipVersion left, MembershipVersion right) => left.version != right.version;
        public static bool operator >=(MembershipVersion left, MembershipVersion right) => left.version >= right.version;
        public static bool operator <=(MembershipVersion left, MembershipVersion right) => left.version <= right.version;
        public static bool operator >(MembershipVersion left, MembershipVersion right) => left.version > right.version;
        public static bool operator <(MembershipVersion left, MembershipVersion right) => left.version < right.version;
    }

    [Serializable]
    public sealed class ClusterMembershipUpdate
    {
        public ClusterMembershipUpdate(ClusterMembershipSnapshot snapshot, ImmutableArray<ClusterMember> changes)
        {
            this.Snapshot = snapshot;
            this.Changes = changes;
        }

        public bool HasChanges => !this.Changes.IsDefaultOrEmpty;
        public ImmutableArray<ClusterMember> Changes { get; }
        public ClusterMembershipSnapshot Snapshot { get; }
    }

    [Serializable]
    public sealed class ClusterMember : IEquatable<ClusterMember>
    {

        public ClusterMember(SiloAddress siloAddress, SiloStatus status, string name)
        {
            this.SiloAddress = siloAddress ?? throw new ArgumentNullException(nameof(siloAddress));
            this.Status = status;
            this.Name = name;
        }

        public string Name { get; }
        public SiloAddress SiloAddress { get; }
        public SiloStatus Status { get; }

        public override bool Equals(object obj) => this.Equals(obj as ClusterMember);

        public bool Equals(ClusterMember other) => other != null && this.SiloAddress.Equals(other.SiloAddress) && this.Status == other.Status;

        public override int GetHashCode() => this.SiloAddress.GetConsistentHashCode();
    }

    [Serializable]
    public sealed class ClusterMembershipSnapshot
    {
        private SiloAddress localSiloAddress;
        public ClusterMembershipSnapshot(SiloAddress localSiloAddress, ImmutableDictionary<SiloAddress, ClusterMember> members, MembershipVersion version)
        {
            this.localSiloAddress = localSiloAddress;
            this.Members = members;
            this.Version = version;
        }

        public static ClusterMembershipSnapshot FromTableData(SiloAddress siloAddress, MembershipTableData data)
        {
            var memberBuilder = ImmutableDictionary.CreateBuilder<SiloAddress, ClusterMember>();
            foreach (var member in data.Members)
            {
                var entry = member.Item1;
                memberBuilder[entry.SiloAddress] = new ClusterMember(entry.SiloAddress, entry.Status, entry.SiloName);
            }

            return new ClusterMembershipSnapshot(siloAddress, memberBuilder.ToImmutable(), new MembershipVersion(data.Version.Version));
        }

        public ClusterMember LocalSilo => this.Members[this.localSiloAddress];

        public ImmutableDictionary<SiloAddress, ClusterMember> Members { get; }

        public MembershipVersion Version { get; }

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
