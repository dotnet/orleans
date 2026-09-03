using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Google.Cloud.Firestore;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Configuration;

namespace Orleans.Clustering.Firestore;

internal partial class FirestoreMembershipTable : IMembershipTable
{
    private const string ClusterGroup = "Cluster";
    private readonly FirestoreOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly string _clusterId;
    private readonly string _partitionId;
    private FirestoreDataManager _storage = default!;

    public FirestoreMembershipTable(
        ILoggerFactory loggerFactory,
        IOptions<FirestoreOptions> options,
        IOptions<ClusterOptions> clusterOptions)
    {
        this._loggerFactory = loggerFactory;
        this._logger = loggerFactory.CreateLogger<FirestoreMembershipTable>();
        this._options = options.Value;
        this._clusterId = clusterOptions.Value.ClusterId;
        this._partitionId = Utils.SanitizeId(this._clusterId);
    }

    public async Task InitializeMembershipTable(bool tryInitTableVersion)
    {
        this._storage = CreateDataManager(this._clusterId);
        await this._storage.Initialize();

        if (tryInitTableVersion)
        {
            var created = await TryCreateTableVersionEntry();
            if (created) LogCreatedTableVersion();
        }
    }

    public async Task DeleteMembershipTableEntries(string clusterId)
    {
        var storage = clusterId == this._clusterId ? this._storage : CreateDataManager(clusterId);
        if (!ReferenceEquals(storage, this._storage))
        {
            await storage.Initialize();
        }

        await storage.ClearCollection();
    }

    public async Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
    {
        var entities = await this._storage.ReadAllEntities<SiloInstanceEntity>();
        var defunctEntries = entities
            .Where(entity => entity.Id != this._partitionId)
            .Where(entity => entity.Status != (int)SiloStatus.Active)
            .Where(entity => GetEffectiveUpdateTime(entity) < beforeDate)
            .ToArray();

        await Task.WhenAll(defunctEntries
            .Chunk(FirestoreDataManager.MaxBatchSize)
            .Select(chunk => this._storage.DeleteEntities(chunk)));
    }

    public async Task<MembershipTableData> ReadRow(SiloAddress key)
    {
        try
        {
            var collection = this._storage.GetCollection();
            var data = await this._storage.ExecuteTransaction(async transaction =>
            {
                var versionSnapshot = await transaction.GetSnapshotAsync(
                    collection.Document(this._partitionId),
                    transaction.CancellationToken);
                var siloSnapshot = await transaction.GetSnapshotAsync(
                    collection.Document(key.ToParsableString()),
                    transaction.CancellationToken);
                if (!versionSnapshot.Exists)
                    throw new KeyNotFoundException($"Could not find cluster version entry for {this._partitionId}");

                var silos = siloSnapshot.Exists
                    ? new[] { siloSnapshot.ConvertTo<SiloInstanceEntity>() }
                    : Array.Empty<SiloInstanceEntity>();
                return (silos, versionSnapshot.ConvertTo<ClusterVersionEntity>());
            });

            var table = Convert(data);

            LogReadEntry(key, table);

            return table;
        }
        catch (Exception exc)
        {
            LogReadEntryError(exc, key);
            throw;
        }
    }

    public async Task<MembershipTableData> ReadAll()
    {
        try
        {
            var collection = this._storage.GetCollection();
            var entries = await this._storage.ExecuteTransaction(async transaction =>
            {
                // RunQuery streams its response, but the transaction binds every document to one
                // serializable snapshot so membership rows cannot be torn from the version row.
                var snapshot = await transaction.GetSnapshotAsync(collection, transaction.CancellationToken);
                var versionSnapshot = snapshot.Documents.SingleOrDefault(document => document.Id == this._partitionId)
                    ?? throw new KeyNotFoundException($"Could not find cluster version entry for {this._partitionId}");
                var silos = snapshot.Documents
                    .Where(document => document.Id != this._partitionId)
                    .Select(document => document.ConvertTo<SiloInstanceEntity>())
                    .ToArray();
                return (silos, versionSnapshot.ConvertTo<ClusterVersionEntity>());
            });
            var data = Convert(entries);
            LogReadAll(data);

            return data;
        }
        catch (Exception exc)
        {
            LogReadAllError(exc);
            throw;
        }
    }

    public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
    {
        try
        {
            LogInsertRow(entry, tableVersion);

            var silo = SiloInstanceEntity.FromMembershipEntry(entry, tableVersion.Version);
            var version = CreateClusterVersionEntity(tableVersion.Version);
            version.ETag = Utils.ParseTimestamp(tableVersion.VersionEtag);

            var collection = this._storage.GetCollection();
            var siloReference = collection.Document(silo.Id);
            var versionReference = collection.Document(this._partitionId);
            bool result;
            try
            {
                result = await this._storage.ExecuteTransaction(transaction =>
                {
                    transaction.Create(siloReference, silo);
                    transaction.Update(versionReference, version.GetFields(), Precondition.LastUpdated(version.ETag.Value));
                    return Task.FromResult(true);
                });
            }
            catch (RpcException exception) when (IsContention(exception))
            {
                result = false;
            }

            if (result == false)
                LogInsertContention(entry, tableVersion);
            return result;
        }
        catch (Exception exc)
        {
            LogInsertError(exc, entry, tableVersion);
            throw;
        }
    }

    public async Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
    {
        try
        {
            LogUpdateRow(entry, etag, tableVersion);

            var silo = SiloInstanceEntity.FromMembershipEntry(entry, tableVersion.Version);
            silo.ETag = Utils.ParseTimestamp(etag);
            var version = CreateClusterVersionEntity(tableVersion.Version);
            version.ETag = Utils.ParseTimestamp(tableVersion.VersionEtag);

            var collection = this._storage.GetCollection();
            var siloReference = collection.Document(silo.Id);
            var versionReference = collection.Document(this._partitionId);
            bool result;
            try
            {
                result = await this._storage.ExecuteTransaction(transaction =>
                {
                    transaction.Update(siloReference, silo.GetFields(), Precondition.LastUpdated(silo.ETag.Value));
                    transaction.Update(versionReference, version.GetFields(), Precondition.LastUpdated(version.ETag.Value));
                    return Task.FromResult(true);
                });
            }
            catch (RpcException exception) when (IsContention(exception))
            {
                result = false;
            }

            if (result == false)
                LogUpdateContention(entry, etag, tableVersion);
            return result;
        }
        catch (Exception exc)
        {
            LogUpdateError(exc, entry, tableVersion);
            throw;
        }
    }

    public async Task UpdateIAmAlive(MembershipEntry entry)
    {
        try
        {
            LogMergeEntry(entry);

            var id = entry.SiloAddress.ToParsableString();
            var iAmAliveTime = new DateTimeOffset(DateTime.SpecifyKind(entry.IAmAliveTime, DateTimeKind.Utc));
            var document = this._storage.GetCollection().Document(id);
            await this._storage.ExecuteTransaction(async transaction =>
            {
                var snapshot = await transaction.GetSnapshotAsync(document, transaction.CancellationToken);
                if (!snapshot.Exists)
                    throw new KeyNotFoundException($"Could not find silo entry for {id}");

                if (snapshot.ConvertTo<SiloInstanceEntity>().IAmAliveTime >= iAmAliveTime)
                {
                    return false;
                }

                transaction.Update(document, new Dictionary<string, object?>
                {
                    [nameof(SiloInstanceEntity.IAmAliveTime)] = iAmAliveTime,
                });
                return true;
            });
        }
        catch (Exception exc)
        {
            LogUpdateIAmAliveError(exc, entry);
            throw;
        }
    }

    private FirestoreDataManager CreateDataManager(string clusterId) => new(
        ClusterGroup,
        Utils.SanitizeId(clusterId),
        this._options,
        this._loggerFactory.CreateLogger<FirestoreDataManager>());

    private ClusterVersionEntity CreateClusterVersionEntity(int version) => new()
    {
        Id = this._partitionId,
        MembershipVersion = version,
    };

    private async Task<bool> TryCreateTableVersionEntry()
    {
        try
        {
            await this._storage.CreateEntity(CreateClusterVersionEntity(0));
            return true;
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.AlreadyExists)
        {
            return false;
        }
    }

    private static bool IsContention(RpcException exception) =>
        exception.StatusCode is StatusCode.Aborted or StatusCode.AlreadyExists or StatusCode.FailedPrecondition or StatusCode.NotFound;

    private static DateTimeOffset GetEffectiveUpdateTime(SiloInstanceEntity entity)
    {
        var result = entity.StartTime > entity.IAmAliveTime ? entity.StartTime : entity.IAmAliveTime;
        if (entity.SuspectingTimes is { Length: > 0 })
        {
            var latestSuspectTime = entity.SuspectingTimes.Max();
            if (latestSuspectTime > result)
            {
                result = latestSuspectTime;
            }
        }

        return result;
    }


    private static MembershipTableData Convert((SiloInstanceEntity[] Silos, ClusterVersionEntity Version) data)
    {
        return new MembershipTableData
        (
            data.Silos.Select(s => Tuple.Create(s.ToMembershipEntry(), Utils.FormatTimestamp(s.ETag!.Value))).ToList(),
            data.Version.ToTableVersion()
        );
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Created new table version row.")]
    private partial void LogCreatedTableVersion();

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Read my entry {SiloAddress} Table={Data}")]
    private partial void LogReadEntry(SiloAddress siloAddress, MembershipTableData data);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Intermediate error reading silo entry for key {SiloAddress} from Firestore.")]
    private partial void LogReadEntryError(Exception exception, SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "ReadAll Table={Data}")]
    private partial void LogReadAll(MembershipTableData data);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Intermediate error reading all silo entries from Firestore.")]
    private partial void LogReadAllError(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "InsertRow entry = {Data}, table version = {TableVersion}")]
    private partial void LogInsertRow(MembershipEntry data, TableVersion tableVersion);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Insert failed due to contention on the table. Will retry. Entry {Data}, table version = {TableVersion}")]
    private partial void LogInsertContention(MembershipEntry data, TableVersion tableVersion);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Intermediate error inserting entry {Data} tableVersion {TableVersion} to Firestore.")]
    private partial void LogInsertError(Exception exception, MembershipEntry data, TableVersion tableVersion);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "UpdateRow entry = {Data}, etag = {ETag}, table version = {TableVersion}")]
    private partial void LogUpdateRow(MembershipEntry data, string etag, TableVersion tableVersion);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Update failed due to contention on the table. Will retry. Entry {Data}, eTag {ETag}, table version = {TableVersion}")]
    private partial void LogUpdateContention(MembershipEntry data, string etag, TableVersion tableVersion);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Intermediate error updating entry {Data} tableVersion {TableVersion} to Firestore.")]
    private partial void LogUpdateError(Exception exception, MembershipEntry data, TableVersion tableVersion);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Merge entry = {Data}")]
    private partial void LogMergeEntry(MembershipEntry data);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Intermediate error updating IAmAlive field for entry {Data} to Firestore.")]
    private partial void LogUpdateIAmAliveError(Exception exception, MembershipEntry data);
}
