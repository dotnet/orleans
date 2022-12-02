using System;
using System.Threading.Tasks;
using Orleans.Runtime;
using StackExchange.Redis;
using Orleans.Configuration;
using Newtonsoft.Json;
using System.Linq;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using System.Globalization;

namespace Orleans.Clustering.Redis
{
    internal class RedisMembershipTable : IMembershipTable, IDisposable
    {
        private const string TableVersionKey = "Version";
        private static readonly TableVersion DefaultTableVersion = new TableVersion(0, "0");
        private readonly RedisClusteringOptions _redisOptions;
        private readonly ClusterOptions _clusterOptions;
        private readonly JsonSerializerSettings _jsonSerializerSettings;
        private readonly RedisKey _clusterKey;
        private IConnectionMultiplexer _muxer;
        private IDatabase _db;

        public RedisMembershipTable(IOptions<RedisClusteringOptions> redisOptions, IOptions<ClusterOptions> clusterOptions)
        {
            _redisOptions = redisOptions.Value;
            _clusterOptions = clusterOptions.Value;
            _clusterKey = $"{_clusterOptions.ServiceId}/{_clusterOptions.ClusterId}";
            _jsonSerializerSettings = JsonSettings.JsonSerializerSettings;
        }

        public bool IsInitialized { get; private set; }

        public async Task DeleteMembershipTableEntries(string clusterId)
        {
            await _db.KeyDeleteAsync(_clusterKey);
        }

        public async Task InitializeMembershipTable(bool tryInitTableVersion)
        {
            _muxer = await _redisOptions.CreateMultiplexer(_redisOptions);
            _db = _muxer.GetDatabase(_redisOptions.Database);

            if (tryInitTableVersion)
            {
                await _db.HashSetAsync(_clusterKey, TableVersionKey, SerializeVersion(DefaultTableVersion), When.NotExists);
            }

            this.IsInitialized = true;
        }

        public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            return await UpsertRowInternal(entry, tableVersion, updateTableVersion: true, allowInsertOnly: true) == UpsertResult.Success;
        }

        private async Task<UpsertResult> UpsertRowInternal(MembershipEntry entry, TableVersion tableVersion, bool updateTableVersion, bool allowInsertOnly)
        {
            var tx = _db.CreateTransaction();
            var rowKey = entry.SiloAddress.ToString();

            if (updateTableVersion)
            {
                if (tableVersion.Version == 0 && "0".Equals(tableVersion.VersionEtag, StringComparison.Ordinal))
                {
                    await _db.HashSetAsync(_clusterKey, TableVersionKey, SerializeVersion(tableVersion), When.NotExists);
                }

                tx.HashSetAsync(_clusterKey, TableVersionKey, SerializeVersion(tableVersion)).Ignore();
            }

            var versionCondition = tx.AddCondition(Condition.HashEqual(_clusterKey, TableVersionKey, SerializeVersion(Predeccessor(tableVersion))));
            ConditionResult insertCondition = null;
            if (allowInsertOnly)
            {
                insertCondition = tx.AddCondition(Condition.HashNotExists(_clusterKey, rowKey));
            }

            tx.HashSetAsync(_clusterKey, rowKey, Serialize(entry)).Ignore();

            var success = await tx.ExecuteAsync();

            if (success)
            {
                return UpsertResult.Success;
            }

            if (!versionCondition.WasSatisfied)
            {
                return UpsertResult.Conflict;
            }

            if (!insertCondition.WasSatisfied)
            {
                return UpsertResult.Failure;
            }

            return UpsertResult.Failure;
        }

        public async Task<MembershipTableData> ReadAll()
        {
            var all = await _db.HashGetAllAsync(_clusterKey);
            var tableVersionRow = all.SingleOrDefault(h => TableVersionKey.Equals(h.Name, StringComparison.Ordinal));
            TableVersion tableVersion = GetTableVersionFromRow(tableVersionRow.Value);

            var data = all.Where(h => !TableVersionKey.Equals(h.Name, StringComparison.Ordinal) && h.Value.HasValue)
                .Select(x => Tuple.Create(Deserialize(x.Value), tableVersion.VersionEtag))
                .ToList();
            return new MembershipTableData(data, tableVersion);
        }

        private static TableVersion GetTableVersionFromRow(RedisValue tableVersionRow)
        {
            return tableVersionRow.HasValue ? DeserializeVersion(tableVersionRow) : DefaultTableVersion;
        }

        public async Task<MembershipTableData> ReadRow(SiloAddress key)
        {
            var tx = _db.CreateTransaction();
            var tableVersionRowTask = tx.HashGetAsync(_clusterKey, TableVersionKey);
            var entryRowTask = tx.HashGetAsync(_clusterKey, key.ToString());
            if (!await tx.ExecuteAsync())
            {
                throw new RedisClusteringException($"Unexpected transaction failure while reading key {key}");
            }

            TableVersion tableVersion = GetTableVersionFromRow(await tableVersionRowTask);
            var entryRow = await entryRowTask;
            if (entryRow.HasValue)
            {
                var entry = Deserialize(entryRow);
                return new MembershipTableData(Tuple.Create(entry, tableVersion.VersionEtag), tableVersion);
            }
            else
            {
                return new MembershipTableData(tableVersion);
            }
        }

        public async Task UpdateIAmAlive(MembershipEntry entry)
        {
            var key = entry.SiloAddress.ToString();
            var tx = _db.CreateTransaction();
            var tableVersionRowTask = tx.HashGetAsync(_clusterKey, TableVersionKey);
            var entryRowTask = tx.HashGetAsync(_clusterKey, key);
            if (!await tx.ExecuteAsync())
            {
                throw new RedisClusteringException($"Unexpected transaction failure while reading key {key}");
            }

            var entryRow = await entryRowTask;
            if (!entryRow.HasValue)
            {
                throw new RedisClusteringException($"Could not find a value for the key {key}");
            }

            TableVersion tableVersion = GetTableVersionFromRow(await tableVersionRowTask).Next();
            var existingEntry = Deserialize(entryRow);

            // Update only the IAmAliveTime property.
            existingEntry.IAmAliveTime = entry.IAmAliveTime;

            var result = await UpsertRowInternal(existingEntry, tableVersion, updateTableVersion: false, allowInsertOnly: false);
            if (result == UpsertResult.Conflict)
            {
                throw new RedisClusteringException($"Failed to update IAmAlive value for key {key} due to conflict");
            }
            else if (result != UpsertResult.Success)
            {
                throw new RedisClusteringException($"Failed to update IAmAlive value for key {key} for an unknown reason");
            }
        }

        public async Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            return await UpsertRowInternal(entry, tableVersion, updateTableVersion: true, allowInsertOnly: false) == UpsertResult.Success;
        }

        public async Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
        {
            var entries = await this.ReadAll();
            foreach (var (entry, _) in entries.Members)
            {
                if (entry.Status == SiloStatus.Dead
                    && new DateTime(Math.Max(entry.IAmAliveTime.Ticks, entry.StartTime.Ticks), DateTimeKind.Utc) < beforeDate)
                {
                    await _db.HashDeleteAsync(_clusterKey, entry.SiloAddress.ToString());
                }
            }
        }

        public void Dispose()
        {
            _muxer?.Dispose();
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
            return JsonConvert.DeserializeObject<MembershipEntry>(json, _jsonSerializerSettings);
        }
    }
}