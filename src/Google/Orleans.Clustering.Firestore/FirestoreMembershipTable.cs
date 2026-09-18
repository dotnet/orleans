using System;
using System.Linq;
using System.Threading;
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

    [Obsolete("Use InitializeMembershipTableAsync instead.")]
    public Task InitializeMembershipTable(bool tryInitTableVersion) => InitializeMembershipTableAsync(tryInitTableVersion, CancellationToken.None);

    public async Task InitializeMembershipTableAsync(bool tryInitTableVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this._storage = CreateDataManager(this._clusterId);
        await this._storage.Initialize(cancellationToken);

        if (tryInitTableVersion)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var created = await TryCreateTableVersionEntry(cancellationToken);
            if (created) LogCreatedTableVersion();
        }
    }

    [Obsolete("Use DeleteMembershipTableEntriesAsync instead.")]
    public Task DeleteMembershipTableEntries(string clusterId) => DeleteMembershipTableEntriesAsync(clusterId, CancellationToken.None);

    public async Task DeleteMembershipTableEntriesAsync(string clusterId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var storage = clusterId == this._clusterId ? this._storage : CreateDataManager(clusterId);
        if (!ReferenceEquals(storage, this._storage))
        {
            await storage.Initialize(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await storage.ClearCollection(cancellationToken);
    }

    [Obsolete("Use CleanupDefunctSiloEntriesAsync instead.")]
    public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => CleanupDefunctSiloEntriesAsync(beforeDate, CancellationToken.None);

    public async Task CleanupDefunctSiloEntriesAsync(DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = this._storage.GetCollection()
            .WhereEqualTo(nameof(SiloInstanceEntity.Status), (int)SiloStatus.Dead);
        var snapshot = await FirestoreDataManager.ExecuteWithCancellation(
            query.GetSnapshotAsync(cancellationToken), cancellationToken);
        var defunct = snapshot.Documents
            .Where(document => GetEffectiveUpdateTime(document.ConvertTo<SiloInstanceEntity>()) < beforeDate);
        await Task.WhenAll(defunct.Chunk(FirestoreDataManager.MaxBatchSize).Select(DeleteBatch));

        async Task DeleteBatch(DocumentSnapshot[] candidates)
        {
            var batch = query.Database.StartBatch();
            foreach (var document in candidates)
            {
                batch.Delete(document.Reference, Precondition.LastUpdated(document.UpdateTime!.Value));
            }

            try
            {
                await FirestoreDataManager.ExecuteWithCancellation(batch.CommitAsync(cancellationToken), cancellationToken);
            }
            catch (RpcException exception) when (exception.StatusCode is StatusCode.Aborted or StatusCode.FailedPrecondition or StatusCode.NotFound)
            {
                // Refresh only a conflicted batch. Native transaction retries protect its new eligibility checks.
                await this._storage.ExecuteTransaction(async transaction =>
                {
                    var current = await transaction.GetAllSnapshotsAsync(
                        candidates.Select(document => document.Reference), transaction.CancellationToken);
                    foreach (var document in current)
                    {
                        if (!document.Exists)
                        {
                            continue;
                        }

                        var entity = document.ConvertTo<SiloInstanceEntity>();
                        if (entity.Status == (int)SiloStatus.Dead && GetEffectiveUpdateTime(entity) < beforeDate)
                        {
                            transaction.Delete(document.Reference, Precondition.LastUpdated(document.UpdateTime!.Value));
                        }
                    }

                    return true;
                }, cancellationToken);
            }
        }
    }

    [Obsolete("Use ReadRowAsync instead.")]
    public Task<MembershipTableData> ReadRow(SiloAddress key) => ReadRowAsync(key, CancellationToken.None);

    public async Task<MembershipTableData> ReadRowAsync(SiloAddress key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var collection = this._storage.GetCollection();
            // One BatchGet stream is a strongly consistent snapshot; the SDK restores request order.
            var snapshots = await FirestoreDataManager.ExecuteWithCancellation(
                collection.Database.GetAllSnapshotsAsync(
                    [collection.Document(this._partitionId), collection.Document(key.ToParsableString())],
                    cancellationToken),
                cancellationToken);
            var versionSnapshot = snapshots[0];
            var siloSnapshot = snapshots[1];
            if (!versionSnapshot.Exists)
                throw new KeyNotFoundException($"Could not find cluster version entry for {this._partitionId}");

            var silos = siloSnapshot.Exists
                ? new[] { siloSnapshot.ConvertTo<SiloInstanceEntity>() }
                : Array.Empty<SiloInstanceEntity>();
            var table = Convert((silos, versionSnapshot.ConvertTo<ClusterVersionEntity>()));

            LogReadEntry(key, table);

            return table;
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            LogReadEntryError(exc, key);
            throw;
        }
    }

    [Obsolete("Use ReadAllAsync instead.")]
    public Task<MembershipTableData> ReadAll() => ReadAllAsync(CancellationToken.None);

    public async Task<MembershipTableData> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var collection = this._storage.GetCollection();
            // A single RunQuery stream includes rows and version in one strongly consistent snapshot.
            var snapshot = await FirestoreDataManager.ExecuteWithCancellation(
                collection.GetSnapshotAsync(cancellationToken), cancellationToken);
            var versionSnapshot = snapshot.Documents.SingleOrDefault(document => document.Id == this._partitionId)
                ?? throw new KeyNotFoundException($"Could not find cluster version entry for {this._partitionId}");
            var silos = snapshot.Documents
                .Where(document => document.Id != this._partitionId)
                .Select(document => document.ConvertTo<SiloInstanceEntity>())
                .ToArray();
            var data = Convert((silos, versionSnapshot.ConvertTo<ClusterVersionEntity>()));
            LogReadAll(data);

            return data;
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            LogReadAllError(exc);
            throw;
        }
    }

    [Obsolete("Use InsertRowAsync instead.")]
    public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => InsertRowAsync(entry, tableVersion, CancellationToken.None);

    public async Task<bool> InsertRowAsync(MembershipEntry entry, TableVersion tableVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
                var batch = collection.Database.StartBatch();
                batch.Create(siloReference, silo);
                batch.Update(versionReference, version.GetFields(), Precondition.LastUpdated(version.ETag.Value));
                await FirestoreDataManager.ExecuteWithCancellation(batch.CommitAsync(cancellationToken), cancellationToken);
                result = true;
            }
            catch (RpcException exception) when (IsContention(exception))
            {
                result = false;
            }

            if (result == false)
                LogInsertContention(entry, tableVersion);
            return result;
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            LogInsertError(exc, entry, tableVersion);
            throw;
        }
    }

    [Obsolete("Use UpdateRowAsync instead.")]
    public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => UpdateRowAsync(entry, etag, tableVersion, CancellationToken.None);

    public async Task<bool> UpdateRowAsync(MembershipEntry entry, string etag, TableVersion tableVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            LogUpdateRow(entry, etag, tableVersion);

            var silo = SiloInstanceEntity.FromMembershipEntry(entry, tableVersion.Version);
            var version = CreateClusterVersionEntity(tableVersion.Version);
            version.ETag = Utils.ParseTimestamp(tableVersion.VersionEtag);

            var collection = this._storage.GetCollection();
            var siloReference = collection.Document(silo.Id);
            var versionReference = collection.Document(this._partitionId);
            var result = false;
            if (string.Equals(etag, tableVersion.VersionEtag, StringComparison.Ordinal))
            {
                try
                {
                    var batch = collection.Database.StartBatch();
                    batch.Update(siloReference, silo.GetFields(), Precondition.MustExist);
                    batch.Update(versionReference, version.GetFields(), Precondition.LastUpdated(version.ETag.Value));
                    await FirestoreDataManager.ExecuteWithCancellation(batch.CommitAsync(cancellationToken), cancellationToken);
                    result = true;
                }
                catch (RpcException exception) when (IsContention(exception))
                {
                    result = false;
                }
            }

            if (result == false)
                LogUpdateContention(entry, etag, tableVersion);
            return result;
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            LogUpdateError(exc, entry, tableVersion);
            throw;
        }
    }

    [Obsolete("Use UpdateIAmAliveAsync instead.")]
    public Task UpdateIAmAlive(MembershipEntry entry) => UpdateIAmAliveAsync(entry, CancellationToken.None);

    public async Task UpdateIAmAliveAsync(MembershipEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            LogMergeEntry(entry);

            var id = entry.SiloAddress.ToParsableString();
            var iAmAliveTime = new DateTimeOffset(DateTime.SpecifyKind(entry.IAmAliveTime, DateTimeKind.Utc));
            var document = this._storage.GetCollection().Document(id);
            await document.UpdateAsync(
                nameof(SiloInstanceEntity.IAmAliveTime), iAmAliveTime, cancellationToken: cancellationToken);
        }
        catch (RpcException exception) when (
            cancellationToken.IsCancellationRequested && exception.StatusCode == StatusCode.Cancelled)
        {
            throw new OperationCanceledException("The Firestore operation was canceled.", exception, cancellationToken);
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
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

    private async Task<bool> TryCreateTableVersionEntry(CancellationToken cancellationToken)
    {
        try
        {
            await this._storage.CreateEntity(CreateClusterVersionEntity(0), cancellationToken);
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
        // Row tokens identify the canonical table version, which advances only with membership mutations.
        var version = data.Version.ToTableVersion();
        return new MembershipTableData
        (
            data.Silos.Select(s => Tuple.Create(s.ToMembershipEntry(), version.VersionEtag)).ToList(),
            version
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
