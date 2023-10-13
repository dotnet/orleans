using Orleans.Runtime;
using Orleans;
using System.Collections.Immutable;

namespace NonSilo.Tests.Membership
{
    /// <summary>
    /// An in-memory implementation of <see cref="IMembershipTable"/> for testing purposes.
    /// </summary>
    public class InMemoryMembershipTable : IMembershipTable
    {
        private readonly object tableLock = new object();
        private readonly List<(string, object)> calls = new List<(string, object)>();
        private ImmutableList<(MembershipEntry, string)> entries = ImmutableList<(MembershipEntry, string)>.Empty;
        private TableVersion version = new TableVersion(0, "0");

        public InMemoryMembershipTable() { }

        public InMemoryMembershipTable(TableVersion version, params MembershipEntry[] entries)
        {
            var builder = ImmutableList.CreateBuilder<(MembershipEntry, string)>();
            foreach (var entry in entries)
            {
                builder.Add((entry, version.VersionEtag));
            }

            this.version = version;
            this.entries = builder.ToImmutable();
        }

        public List<(string Method, object Arguments)> Calls
        {
            get
            {
                lock (this.tableLock) return new List<(string, object)>(this.calls);
            }
        }

        public Action OnReadAll { get; set; }

        public void ClearCalls()
        {
            lock (this.tableLock) this.calls.Clear();
        }

        public void Reset()
        {
            lock (this.tableLock)
            {
                this.entries = ImmutableList<(MembershipEntry, string)>.Empty;
                this.version = this.version.Next();
            }
        }

        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
        {
            lock (this.tableLock)
            {
                this.calls.Add((nameof(CleanupDefunctSiloEntries), beforeDate));
                var newEntries = ImmutableList.CreateBuilder<(MembershipEntry, string)>();
                foreach (var (entry, etag) in this.entries)
                {
                    if (entry.Status == SiloStatus.Dead
                        && new DateTime(Math.Max(entry.IAmAliveTime.Ticks, entry.StartTime.Ticks), DateTimeKind.Utc) < beforeDate)
                    {
                        continue;
                    }

                    newEntries.Add((entry, etag));
                }

                this.entries = newEntries.ToImmutable();
            }

            return Task.CompletedTask;
        }

        public Task DeleteMembershipTableEntries(string clusterId)
        {
            lock (this.tableLock)
            {
                this.calls.Add((nameof(DeleteMembershipTableEntries), clusterId));
            }

            return Task.CompletedTask;
        }

        public Task InitializeMembershipTable(bool tryInitTableVersion)
        {
            lock (this.tableLock)
            {
                this.calls.Add((nameof(InitializeMembershipTable), tryInitTableVersion));
            }

            return Task.CompletedTask;
        }

        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            lock (this.tableLock)
            {
                this.calls.Add((nameof(InsertRow), (entry, tableVersion)));
                this.ValidateVersion(tableVersion);

                if (this.entries.Any(e => e.Item1.SiloAddress.Equals(entry.SiloAddress)))
                {
                    return Task.FromResult(false);
                }

                this.version = new TableVersion(tableVersion.Version, tableVersion.Version.ToString());
                this.entries = this.entries.Add((entry, this.version.VersionEtag));

                return Task.FromResult(true);
            }
        }

        public Task<MembershipTableData> ReadAll()
        {
            this.OnReadAll?.Invoke();
            lock (this.tableLock)
            {
                this.calls.Add((nameof(ReadAll), null));
                var result = new MembershipTableData(
                    this.entries.Select(e => Tuple.Create(e.Item1, e.Item2)).ToList(),
                    this.version);
                return Task.FromResult(result);
            }
        }

        public Task<MembershipTableData> ReadRow(SiloAddress key)
        {
            lock (this.tableLock)
            {
                this.calls.Add((nameof(ReadRow), key));
                var result = new MembershipTableData(
                    this.entries.Where(e => e.Item1.SiloAddress.Equals(key)).Select(e => Tuple.Create(e.Item1, e.Item2)).ToList(),
                    this.version);
                return Task.FromResult(result);
            }
        }

        public Task UpdateIAmAlive(MembershipEntry entry)
        {
            lock (this.tableLock)
            {
                this.calls.Add((nameof(UpdateIAmAlive), entry));
                var existingEntry = this.entries.Single(e => e.Item1.SiloAddress.Equals(entry.SiloAddress));
                var replacement = existingEntry.Item1.Copy();
                replacement.IAmAliveTime = entry.IAmAliveTime;
                this.entries = this.entries.Replace(existingEntry, (replacement, Guid.NewGuid().ToString()));
                return Task.CompletedTask;
            }
        }

        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            lock (this.tableLock)
            {
                this.calls.Add((nameof(UpdateRow), (entry, etag, tableVersion)));
                this.ValidateVersion(tableVersion);
                var existingEntry = this.entries.FirstOrDefault(e => e.Item1.SiloAddress.Equals(entry.SiloAddress));
                if (existingEntry.Item1 is null) return Task.FromResult(false);

                if (!etag.Equals(existingEntry.Item2))
                {
                    throw new InvalidOperationException($"Mismatching row etag. Required: {existingEntry.Item2}, Provided: {etag}");
                }

                this.version = new TableVersion(tableVersion.Version, tableVersion.Version.ToString());
                this.entries = this.entries.Replace(existingEntry, (entry, this.version.VersionEtag));
                return Task.FromResult(true);
            }
        }

        private void ValidateVersion(TableVersion tableVersion)
        {
            lock (this.tableLock)
            {
                if (this.version.VersionEtag != tableVersion.VersionEtag)
                {
                    throw new InvalidOperationException("Etag mismatch");
                }

                if (this.version.Version >= tableVersion.Version)
                {
                    throw new InvalidOperationException("Version must increase on update");
                }
            }
        }
    }
}
