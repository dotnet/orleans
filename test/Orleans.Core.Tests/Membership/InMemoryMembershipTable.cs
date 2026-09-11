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
        private readonly List<(string, object?)> calls = new List<(string, object?)>();
        private ImmutableList<(MembershipEntry, string)> entries = ImmutableList<(MembershipEntry, string)>.Empty;

        public InMemoryMembershipTable() { }

        public InMemoryMembershipTable(TableVersion version, params MembershipEntry[] entries)
        {
            var builder = ImmutableList.CreateBuilder<(MembershipEntry, string)>();
            foreach (var entry in entries)
            {
                builder.Add((entry, version.VersionEtag));
            }

            this.Version = version;
            this.entries = builder.ToImmutable();
        }

        public List<(string Method, object? Arguments)> Calls
        {
            get
            {
                lock (this.tableLock) return new List<(string, object?)>(this.calls);
            }
        }

        public Action? OnReadAll { get; set; }
        public Action<DateTimeOffset>? OnCleanupDefunctSiloEntries { get; set; }
        public TableVersion Version { get; set; } = new TableVersion(0, "0");

        public void ClearCalls()
        {
            lock (this.tableLock) this.calls.Clear();
        }

        public void Reset()
        {
            lock (this.tableLock)
            {
                this.entries = ImmutableList<(MembershipEntry, string)>.Empty;
                this.Version = this.Version.Next();
            }
        }

        [Obsolete("Use CleanupDefunctSiloEntriesAsync instead.")]
        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => CleanupDefunctSiloEntriesAsync(beforeDate, CancellationToken.None);

        public Task CleanupDefunctSiloEntriesAsync(DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.OnCleanupDefunctSiloEntries?.Invoke(beforeDate);
            lock (this.tableLock)
            {
                this.calls.Add((nameof(CleanupDefunctSiloEntriesAsync), beforeDate));
                var newEntries = ImmutableList.CreateBuilder<(MembershipEntry, string)>();
                foreach (var (entry, etag) in this.entries)
                {
                    if (entry.Status == SiloStatus.Dead
                        && entry.EffectiveUpdateTime < beforeDate)
                    {
                        continue;
                    }

                    newEntries.Add((entry, etag));
                }

                this.entries = newEntries.ToImmutable();
            }

            return Task.CompletedTask;
        }

        [Obsolete("Use DeleteMembershipTableEntriesAsync instead.")]
        public Task DeleteMembershipTableEntries(string clusterId) => DeleteMembershipTableEntriesAsync(clusterId, CancellationToken.None);

        public Task DeleteMembershipTableEntriesAsync(string clusterId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.tableLock)
            {
                this.calls.Add((nameof(DeleteMembershipTableEntriesAsync), clusterId));
            }

            return Task.CompletedTask;
        }

        [Obsolete("Use InitializeMembershipTableAsync instead.")]
        public Task InitializeMembershipTable(bool tryInitTableVersion) => InitializeMembershipTableAsync(tryInitTableVersion, CancellationToken.None);

        public Task InitializeMembershipTableAsync(bool tryInitTableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.tableLock)
            {
                this.calls.Add((nameof(InitializeMembershipTableAsync), tryInitTableVersion));
            }

            return Task.CompletedTask;
        }

        [Obsolete("Use InsertRowAsync instead.")]
        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => InsertRowAsync(entry, tableVersion, CancellationToken.None);

        public Task<bool> InsertRowAsync(MembershipEntry entry, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.tableLock)
            {
                this.calls.Add((nameof(InsertRowAsync), (entry, tableVersion)));
                this.ValidateVersion(tableVersion);

                if (this.entries.Exists(e => e.Item1.SiloAddress.Equals(entry.SiloAddress)))
                {
                    return Task.FromResult(false);
                }

                this.Version = new TableVersion(tableVersion.Version, tableVersion.Version.ToString());
                this.entries = this.entries.Add((entry, this.Version.VersionEtag));

                return Task.FromResult(true);
            }
        }

        [Obsolete("Use ReadAllAsync instead.")]
        public Task<MembershipTableData> ReadAll() => ReadAllAsync(CancellationToken.None);

        public Task<MembershipTableData> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.OnReadAll?.Invoke();
            lock (this.tableLock)
            {
                this.calls.Add((nameof(ReadAllAsync), null));
                var result = new MembershipTableData(
                    this.entries.Select(e => Tuple.Create(e.Item1, e.Item2)).ToList(),
                    this.Version);
                return Task.FromResult(result);
            }
        }

        [Obsolete("Use ReadRowAsync instead.")]
        public Task<MembershipTableData> ReadRow(SiloAddress key) => ReadRowAsync(key, CancellationToken.None);

        public Task<MembershipTableData> ReadRowAsync(SiloAddress key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.tableLock)
            {
                this.calls.Add((nameof(ReadRowAsync), key));
                var result = new MembershipTableData(
                    this.entries.Where(e => e.Item1.SiloAddress.Equals(key)).Select(e => Tuple.Create(e.Item1, e.Item2)).ToList(),
                    this.Version);
                return Task.FromResult(result);
            }
        }

        [Obsolete("Use UpdateIAmAliveAsync instead.")]
        public Task UpdateIAmAlive(MembershipEntry entry) => UpdateIAmAliveAsync(entry, CancellationToken.None);

        public Task UpdateIAmAliveAsync(MembershipEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.tableLock)
            {
                this.calls.Add((nameof(UpdateIAmAliveAsync), entry));
                var existingEntry = this.entries.Single(e => e.Item1.SiloAddress.Equals(entry.SiloAddress));
                var replacement = existingEntry.Item1.Copy();
                replacement.IAmAliveTime = entry.IAmAliveTime;
                this.entries = this.entries.Replace(existingEntry, (replacement, Guid.NewGuid().ToString()));
                return Task.CompletedTask;
            }
        }

        [Obsolete("Use UpdateRowAsync instead.")]
        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => UpdateRowAsync(entry, etag, tableVersion, CancellationToken.None);

        public Task<bool> UpdateRowAsync(MembershipEntry entry, string etag, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (this.tableLock)
            {
                this.calls.Add((nameof(UpdateRowAsync), (entry, etag, tableVersion)));
                this.ValidateVersion(tableVersion);
                var existingEntry = this.entries.Find(e => e.Item1.SiloAddress.Equals(entry.SiloAddress));
                if (existingEntry.Item1 is null) return Task.FromResult(false);

                if (!etag.Equals(existingEntry.Item2, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Mismatching row etag. Required: {existingEntry.Item2}, Provided: {etag}");
                }

                this.Version = new TableVersion(tableVersion.Version, tableVersion.Version.ToString());
                this.entries = this.entries.Replace(existingEntry, (entry, this.Version.VersionEtag));
                return Task.FromResult(true);
            }
        }

        private void ValidateVersion(TableVersion tableVersion)
        {
            lock (this.tableLock)
            {
                if (this.Version.VersionEtag != tableVersion.VersionEtag)
                {
                    throw new InvalidOperationException("Etag mismatch");
                }

                if (this.Version.Version >= tableVersion.Version)
                {
                    throw new InvalidOperationException("Version must increase on update");
                }
            }
        }
    }
}
