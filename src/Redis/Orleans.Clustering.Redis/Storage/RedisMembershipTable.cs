using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using StackExchange.Redis;
using Orleans.Configuration;
using Newtonsoft.Json;
using System.Linq;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Clustering.Redis
{
    internal class RedisMembershipTable : IMembershipTable, IDisposable, IAsyncDisposable
    {
        private const string TableVersionKey = "Version";
        private static readonly TableVersion DefaultTableVersion = new TableVersion(0, "0");
        private readonly RedisClusteringOptions _redisOptions;
        private readonly ClusterOptions _clusterOptions;
        private readonly JsonSerializerSettings _jsonSerializerSettings;
        private readonly RedisKey _clusterKey;
        private readonly object _lifecycleLock = new();
        private readonly SemaphoreSlim _initializationLock = new(1, 1);
        private IConnectionMultiplexer _muxer = null!;
        private IDatabase _db = null!;
        private bool _muxerIsShared;
        private bool _disposed;
        private int _initializingCount;

        public RedisMembershipTable(IOptions<RedisClusteringOptions> redisOptions, IOptions<ClusterOptions> clusterOptions)
        {
            _redisOptions = redisOptions.Value;
            _clusterOptions = clusterOptions.Value;
            _clusterKey = _redisOptions.CreateRedisKey(_clusterOptions);
            _jsonSerializerSettings = JsonSettings.JsonSerializerSettings;
        }

        public bool IsInitialized { get; private set; }

        [Obsolete("Use DeleteMembershipTableEntriesAsync instead.")]
        public Task DeleteMembershipTableEntries(string clusterId) => DeleteMembershipTableEntriesAsync(clusterId, CancellationToken.None);

        public async Task DeleteMembershipTableEntriesAsync(string clusterId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(clusterId, _clusterOptions.ClusterId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Cluster id {clusterId} does not match RedisMembershipTable value of '{_clusterOptions.ClusterId}'.",
                    nameof(clusterId));
            }

            await AwaitAsync(_db.KeyDeleteAsync(_clusterKey), cancellationToken);
        }

        [Obsolete("Use InitializeMembershipTableAsync instead.")]
        public Task InitializeMembershipTable(bool tryInitTableVersion) => InitializeMembershipTableAsync(tryInitTableVersion, CancellationToken.None);

        public async Task InitializeMembershipTableAsync(bool tryInitTableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lifecycleLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                // Include semaphore waiters so disposal occurs after every wait and release has finished.
                _initializingCount++;
            }

            try
            {
                await _initializationLock.WaitAsync(cancellationToken);
                try
                {
                    await InitializeMembershipTableCoreAsync(tryInitTableVersion, cancellationToken);
                }
                finally
                {
                    _initializationLock.Release();
                }
            }
            finally
            {
                lock (_lifecycleLock)
                {
                    if (--_initializingCount == 0 && _disposed)
                    {
                        _initializationLock.Dispose();
                    }
                }
            }
        }

        private async Task InitializeMembershipTableCoreAsync(bool tryInitTableVersion, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IConnectionMultiplexer? muxer;
            bool isShared;
            lock (_lifecycleLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                muxer = _muxer;
                isShared = _muxerIsShared;
            }

            var isNewConnection = muxer is null;
            var initialized = false;
            try
            {
                if (muxer is null)
                {
                    var creation = _redisOptions.CreateMultiplexer(_redisOptions);
                    try
                    {
                        (muxer, isShared) = await AwaitAsync(creation, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // The tokenless factory can still return an owned connection after the caller stops waiting.
                        DisposeAbandonedMultiplexerAsync(creation).Ignore();
                        throw;
                    }
                }

                IDatabase db;
                lock (_lifecycleLock)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    db = isNewConnection ? muxer.GetDatabase() : _db;
                }

                if (tryInitTableVersion)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await AwaitAsync(db.HashSetAsync(_clusterKey, TableVersionKey, SerializeVersion(DefaultTableVersion), When.NotExists), cancellationToken);

                    if (_redisOptions.EntryExpiry is { } expiry)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await AwaitAsync(db.KeyExpireAsync(_clusterKey, expiry), cancellationToken);
                    }
                }

                lock (_lifecycleLock)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (isNewConnection)
                    {
                        _muxer = muxer;
                        _muxerIsShared = isShared;
                        _db = db;
                    }

                    IsInitialized = true;
                    initialized = true;
                }
            }
            finally
            {
                if (!initialized && isNewConnection && !isShared && muxer is not null)
                {
                    await muxer.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        private static async Task DisposeAbandonedMultiplexerAsync(Task<(IConnectionMultiplexer Multiplexer, bool IsShared)> creation)
        {
            var (muxer, isShared) = await creation.ConfigureAwait(false);
            if (!isShared)
            {
                await muxer.DisposeAsync().ConfigureAwait(false);
            }
        }

        [Obsolete("Use InsertRowAsync instead.")]
        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => InsertRowAsync(entry, tableVersion, CancellationToken.None);

        public async Task<bool> InsertRowAsync(MembershipEntry entry, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            return await UpsertRowInternal(entry, tableVersion, allowInsertOnly: true, cancellationToken);
        }

        private async Task<bool> UpsertRowInternal(MembershipEntry entry, TableVersion tableVersion, bool allowInsertOnly, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowKey = entry.SiloAddress.ToString();
            var updatedEntry = Deserialize(Serialize(entry));
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RedisValue current = RedisValue.Null;
                if (!allowInsertOnly)
                {
                    var rows = await ReadEntryAsync(rowKey, cancellationToken);
                    var version = GetTableVersionFromRow(rows[0]);
                    current = rows[1];
                    if (!string.Equals(version.VersionEtag, tableVersion.VersionEtag, StringComparison.Ordinal) || !current.HasValue)
                    {
                        return false;
                    }

                    var existingEntry = Deserialize(current.ToString());
                    updatedEntry.IAmAliveTime = new DateTime(Math.Max(entry.IAmAliveTime.Ticks, existingEntry.IAmAliveTime.Ticks), DateTimeKind.Utc);
                }

                var tx = _db.CreateTransaction();
                tx.AddCondition(Condition.HashEqual(_clusterKey, TableVersionKey, tableVersion.VersionEtag));
                tx.AddCondition(allowInsertOnly
                    ? Condition.HashNotExists(_clusterKey, rowKey)
                    : Condition.HashEqual(_clusterKey, rowKey, current));
                tx.HashSetAsync(_clusterKey, TableVersionKey, SerializeVersion(tableVersion)).Ignore();
                tx.HashSetAsync(_clusterKey, rowKey, Serialize(updatedEntry)).Ignore();
                cancellationToken.ThrowIfCancellationRequested();
                if (await AwaitAsync(tx.ExecuteAsync(), cancellationToken))
                {
                    return true;
                }

                if (allowInsertOnly)
                {
                    return false;
                }

                // A heartbeat can change the row while the table version stays unchanged. Merge it and retry.
            }
        }

        [Obsolete("Use ReadAllAsync instead.")]
        public Task<MembershipTableData> ReadAll() => ReadAllAsync(CancellationToken.None);

        public async Task<MembershipTableData> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var all = await AwaitAsync(_db.HashGetAllAsync(_clusterKey), cancellationToken);
            var tableVersionRow = all.SingleOrDefault(h => TableVersionKey.Equals(h.Name, StringComparison.Ordinal));
            TableVersion tableVersion = GetTableVersionFromRow(tableVersionRow.Value);

            var data = all.Where(x => !TableVersionKey.Equals(x.Name, StringComparison.Ordinal) && x.Value.HasValue)
                .Select(x => Tuple.Create(Deserialize(x.Value!), tableVersion.VersionEtag))
                .ToList();
            return new MembershipTableData(data, tableVersion);
        }

        private static TableVersion GetTableVersionFromRow(RedisValue tableVersionRow)
        {
            if (TryGetValueString(tableVersionRow, out var value))
            {
                return DeserializeVersion(value);
            }

            throw new RedisClusteringException("The Redis membership table version is missing. The table may have expired or been deleted.");
        }

        private static bool TryGetValueString(RedisValue key, [NotNullWhen(true)] out string? value)
        {
            if (key.HasValue)
            {
                value = key.ToString();
                return true;
            }

            value = null;
            return false;
        }

        [Obsolete("Use ReadRowAsync instead.")]
        public Task<MembershipTableData> ReadRow(SiloAddress key) => ReadRowAsync(key, CancellationToken.None);

        public async Task<MembershipTableData> ReadRowAsync(SiloAddress key, CancellationToken cancellationToken = default)
        {
            var rows = await ReadEntryAsync(key.ToString(), cancellationToken);
            var tableVersion = GetTableVersionFromRow(rows[0]);
            var entryRow = rows[1];
            if (TryGetValueString(entryRow, out var entryValueString))
            {
                var entry = Deserialize(entryValueString);
                return new MembershipTableData(Tuple.Create(entry, tableVersion.VersionEtag), tableVersion);
            }
            else
            {
                return new MembershipTableData(tableVersion);
            }
        }

        private async Task<RedisValue[]> ReadEntryAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // HMGET reads the version and row from the same Redis command.
            return await AwaitAsync(_db.HashGetAsync(_clusterKey, [TableVersionKey, key]), cancellationToken);
        }

        [Obsolete("Use UpdateIAmAliveAsync instead.")]
        public Task UpdateIAmAlive(MembershipEntry entry) => UpdateIAmAliveAsync(entry, CancellationToken.None);

        public async Task UpdateIAmAliveAsync(MembershipEntry entry, CancellationToken cancellationToken = default)
        {
            var key = entry.SiloAddress.ToString();
            while (true)
            {
                var rows = await ReadEntryAsync(key, cancellationToken);
                _ = GetTableVersionFromRow(rows[0]);
                var current = rows[1];
                if (!current.HasValue)
                {
                    return;
                }

                var existingEntry = Deserialize(current.ToString());
                if (existingEntry.IAmAliveTime >= entry.IAmAliveTime)
                {
                    return;
                }

                existingEntry.IAmAliveTime = entry.IAmAliveTime;
                var tx = _db.CreateTransaction();
                tx.AddCondition(Condition.HashEqual(_clusterKey, key, current));
                tx.HashSetAsync(_clusterKey, key, Serialize(existingEntry)).Ignore();
                cancellationToken.ThrowIfCancellationRequested();
                if (await AwaitAsync(tx.ExecuteAsync(), cancellationToken))
                {
                    return;
                }
            }
        }

        [Obsolete("Use UpdateRowAsync instead.")]
        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => UpdateRowAsync(entry, etag, tableVersion, CancellationToken.None);

        public async Task<bool> UpdateRowAsync(MembershipEntry entry, string etag, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Reads use the table version as the row etag, so both must describe the same view.
            return string.Equals(etag, tableVersion.VersionEtag, StringComparison.Ordinal)
                && await UpsertRowInternal(entry, tableVersion, allowInsertOnly: false, cancellationToken);
        }

        [Obsolete("Use CleanupDefunctSiloEntriesAsync instead.")]
        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => CleanupDefunctSiloEntriesAsync(beforeDate, CancellationToken.None);

        public async Task CleanupDefunctSiloEntriesAsync(DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = await AwaitAsync(_db.HashGetAllAsync(_clusterKey), cancellationToken);
            _ = GetTableVersionFromRow(rows.SingleOrDefault(row => row.Name == TableVersionKey).Value);
            foreach (var row in rows)
            {
                if (row.Name == TableVersionKey)
                {
                    continue;
                }

                var entry = Deserialize(row.Value.ToString());
                if (entry.Status == SiloStatus.Dead
                    && Math.Max(entry.IAmAliveTime.Ticks, entry.StartTime.Ticks) < beforeDate.UtcDateTime.Ticks
                    && entry.SuspectTimes?.Any(vote => vote.Item2 >= beforeDate.UtcDateTime) != true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tx = _db.CreateTransaction();
                    tx.AddCondition(Condition.HashEqual(_clusterKey, row.Name, row.Value));
                    tx.HashDeleteAsync(_clusterKey, row.Name).Ignore();
                    cancellationToken.ThrowIfCancellationRequested();
                    await AwaitAsync(tx.ExecuteAsync(), cancellationToken);
                }
            }
        }

        // StackExchange.Redis does not accept cancellation tokens. Observe terminal faults if a caller abandons its wait.
        private static async Task<T> AwaitAsync<T>(Task<T> operation, CancellationToken cancellationToken)
        {
            operation.Ignore();
            return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            DetachOwnedMultiplexer()?.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (DetachOwnedMultiplexer() is { } muxer)
            {
                await muxer.DisposeAsync().ConfigureAwait(false);
            }
        }

        private IConnectionMultiplexer? DetachOwnedMultiplexer()
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return null;
                }

                _disposed = true;
                if (_initializingCount == 0)
                {
                    _initializationLock.Dispose();
                }

                var ownedMuxer = _muxerIsShared ? null : _muxer;
                _muxer = null!;
                _db = null!;
                _muxerIsShared = false;
                IsInitialized = false;
                return ownedMuxer;
            }
        }

        private static string SerializeVersion(TableVersion tableVersion) => tableVersion.Version.ToString(CultureInfo.InvariantCulture);

        private static TableVersion DeserializeVersion(string versionString)
        {
            var version = int.Parse(versionString, CultureInfo.InvariantCulture);
            return new TableVersion(version, versionString);
        }

        private string Serialize(MembershipEntry value)
        {
            return JsonConvert.SerializeObject(value, _jsonSerializerSettings);
        }

        private MembershipEntry Deserialize(string json)
        {
            return JsonConvert.DeserializeObject<MembershipEntry>(json, _jsonSerializerSettings)!;
        }
    }
}
