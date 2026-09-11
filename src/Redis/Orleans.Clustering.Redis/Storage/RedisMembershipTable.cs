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
        private IConnectionMultiplexer _muxer = null!;
        private IDatabase _db = null!;
        private bool _muxerIsShared;

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
            await AwaitAsync(_db.KeyDeleteAsync(_clusterKey), cancellationToken);
        }

        [Obsolete("Use InitializeMembershipTableAsync instead.")]
        public Task InitializeMembershipTable(bool tryInitTableVersion) => InitializeMembershipTableAsync(tryInitTableVersion, CancellationToken.None);

        public async Task InitializeMembershipTableAsync(bool tryInitTableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var creation = _redisOptions.CreateMultiplexer(_redisOptions);
            IConnectionMultiplexer muxer;
            bool isShared;
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

            var initialized = false;
            try
            {
                var db = muxer.GetDatabase();
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

                _muxer = muxer;
                _muxerIsShared = isShared;
                _db = db;
                IsInitialized = true;
                initialized = true;
            }
            finally
            {
                if (!initialized && !isShared)
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
            return await UpsertRowInternal(entry, tableVersion, updateTableVersion: true, allowInsertOnly: true, cancellationToken) == UpsertResult.Success;
        }

        private async Task<UpsertResult> UpsertRowInternal(MembershipEntry entry, TableVersion tableVersion, bool updateTableVersion, bool allowInsertOnly, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tx = _db.CreateTransaction();
            var rowKey = entry.SiloAddress.ToString();

            if (updateTableVersion)
            {
                tx.HashSetAsync(_clusterKey, TableVersionKey, SerializeVersion(tableVersion)).Ignore();
            }

            var versionCondition = tx.AddCondition(Condition.HashEqual(_clusterKey, TableVersionKey, SerializeVersion(Predeccessor(tableVersion))));

            ConditionResult? insertCondition;
            if (allowInsertOnly)
            {
                insertCondition = tx.AddCondition(Condition.HashNotExists(_clusterKey, rowKey));
            }
            else
            {
                insertCondition = null;
            }

            tx.HashSetAsync(_clusterKey, rowKey, Serialize(entry)).Ignore();

            cancellationToken.ThrowIfCancellationRequested();
            var success = await AwaitAsync(tx.ExecuteAsync(), cancellationToken);

            if (success)
            {
                return UpsertResult.Success;
            }

            if (!versionCondition.WasSatisfied)
            {
                return UpsertResult.Conflict;
            }

            if (insertCondition is not null && !insertCondition.WasSatisfied)
            {
                return UpsertResult.Conflict;
            }

            return UpsertResult.Failure;
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

            return DefaultTableVersion;
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
            cancellationToken.ThrowIfCancellationRequested();
            var tx = _db.CreateTransaction();
            var tableVersionRowTask = tx.HashGetAsync(_clusterKey, TableVersionKey);
            tableVersionRowTask.Ignore();
            var entryRowTask = tx.HashGetAsync(_clusterKey, key.ToString());
            entryRowTask.Ignore();
            cancellationToken.ThrowIfCancellationRequested();
            if (!await AwaitAsync(tx.ExecuteAsync(), cancellationToken))
            {
                throw new RedisClusteringException($"Unexpected transaction failure while reading key {key}");
            }

            TableVersion tableVersion = GetTableVersionFromRow(await AwaitAsync(tableVersionRowTask, cancellationToken));
            var entryRow = await AwaitAsync(entryRowTask, cancellationToken);
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

        [Obsolete("Use UpdateIAmAliveAsync instead.")]
        public Task UpdateIAmAlive(MembershipEntry entry) => UpdateIAmAliveAsync(entry, CancellationToken.None);

        public async Task UpdateIAmAliveAsync(MembershipEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = entry.SiloAddress.ToString();
            var tx = _db.CreateTransaction();
            var tableVersionRowTask = tx.HashGetAsync(_clusterKey, TableVersionKey);
            tableVersionRowTask.Ignore();
            var entryRowTask = tx.HashGetAsync(_clusterKey, key);
            entryRowTask.Ignore();
            cancellationToken.ThrowIfCancellationRequested();
            if (!await AwaitAsync(tx.ExecuteAsync(), cancellationToken))
            {
                throw new RedisClusteringException($"Unexpected transaction failure while reading key {key}");
            }

            var entryRow = await AwaitAsync(entryRowTask, cancellationToken);
            if (!TryGetValueString(entryRow, out var entryRowValue))
            {
                throw new RedisClusteringException($"Could not find a value for the key {key}");
            }

            TableVersion tableVersion = GetTableVersionFromRow(await AwaitAsync(tableVersionRowTask, cancellationToken)).Next();
            var existingEntry = Deserialize(entryRowValue);

            // Update only the IAmAliveTime property.
            existingEntry.IAmAliveTime = entry.IAmAliveTime;

            var result = await UpsertRowInternal(existingEntry, tableVersion, updateTableVersion: false, allowInsertOnly: false, cancellationToken);
            if (result == UpsertResult.Conflict)
            {
                throw new RedisClusteringException($"Failed to update IAmAlive value for key {key} due to conflict");
            }
            else if (result != UpsertResult.Success)
            {
                throw new RedisClusteringException($"Failed to update IAmAlive value for key {key} for an unknown reason");
            }
        }

        [Obsolete("Use UpdateRowAsync instead.")]
        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => UpdateRowAsync(entry, etag, tableVersion, CancellationToken.None);

        public async Task<bool> UpdateRowAsync(MembershipEntry entry, string etag, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            return await UpsertRowInternal(entry, tableVersion, updateTableVersion: true, allowInsertOnly: false, cancellationToken) == UpsertResult.Success;
        }

        [Obsolete("Use CleanupDefunctSiloEntriesAsync instead.")]
        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => CleanupDefunctSiloEntriesAsync(beforeDate, CancellationToken.None);

        public async Task CleanupDefunctSiloEntriesAsync(DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
        {
            var entries = await ReadAllAsync(cancellationToken);
            foreach (var (entry, _) in entries.Members)
            {
                if (entry.Status != SiloStatus.Active
                    && new DateTime(Math.Max(entry.IAmAliveTime.Ticks, entry.StartTime.Ticks), DateTimeKind.Utc) < beforeDate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await AwaitAsync(_db.HashDeleteAsync(_clusterKey, entry.SiloAddress.ToString()), cancellationToken);
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
            var muxer = _muxer;
            if (muxer is null)
            {
                return;
            }

            var muxerIsShared = _muxerIsShared;
            _muxer = null!;
            _db = null!;
            _muxerIsShared = false;

            if (!muxerIsShared)
            {
                muxer.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            var muxer = _muxer;
            if (muxer is null)
            {
                return;
            }

            var muxerIsShared = _muxerIsShared;
            _muxer = null!;
            _db = null!;
            _muxerIsShared = false;

            if (!muxerIsShared)
            {
                await muxer.DisposeAsync().ConfigureAwait(false);
            }
        }

        private enum UpsertResult
        {
            Success = 1,
            Failure = 2,
            Conflict = 3,
        }

        private static string SerializeVersion(TableVersion tableVersion) => tableVersion.Version.ToString(CultureInfo.InvariantCulture);

        private static TableVersion DeserializeVersion(string versionString)
        {
            if (string.IsNullOrWhiteSpace(versionString))
            {
                return DefaultTableVersion;
            }

            var version = int.Parse(versionString);
            return new TableVersion(version, versionString);
        }

        private static TableVersion Predeccessor(TableVersion tableVersion) => new TableVersion(tableVersion.Version - 1, (tableVersion.Version - 1).ToString(CultureInfo.InvariantCulture));


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
