using System.Net;
using System.Threading;
using Orleans.Clustering.Cosmos.Models;

namespace Orleans.Clustering.Cosmos;

internal partial class CosmosMembershipTable : IMembershipTable
{
    private const string PARTITION_KEY = "/ClusterId";
    private const string CLUSTER_VERSION_ID = "ClusterVersion";
    private const int MaxMembershipSnapshotAttempts = 5;
    private const int MaxBatchSize = 100;
    private readonly ILogger _logger;
    private readonly CosmosClusteringOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _clusterId;
    private readonly PartitionKey _partitionKey;
    private Task<CosmosClient>? _clientTask;
    private CosmosClient _client = default!;
    private Container _container = default!;

    public CosmosMembershipTable(
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        IOptions<CosmosClusteringOptions> options,
        IOptions<ClusterOptions> clusterOptions)
    {
        _logger = loggerFactory.CreateLogger<CosmosMembershipTable>();
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _clusterId = clusterOptions.Value.ClusterId;
        _partitionKey = new(_clusterId);
    }

    [Obsolete("Use InitializeMembershipTableAsync instead.")]
    public Task InitializeMembershipTable(bool tryInitTableVersion) => InitializeMembershipTableAsync(tryInitTableVersion, CancellationToken.None);

    public async Task InitializeMembershipTableAsync(bool tryInitTableVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await InitializeCosmosClient(cancellationToken).ConfigureAwait(false);

        if (_options.IsResourceCreationEnabled)
        {
            if (_options.CleanResourcesOnInitialization)
            {
                await TryDeleteDatabase(cancellationToken).ConfigureAwait(false);
            }

            await TryCreateCosmosResources(cancellationToken).ConfigureAwait(false);
        }

        _container = _client.GetContainer(_options.DatabaseName, _options.ContainerName);

        ClusterVersionEntity? versionEntity = null;

        try
        {
            versionEntity = (await _container.ReadItemAsync<ClusterVersionEntity>(
                CLUSTER_VERSION_ID, _partitionKey,
                new ItemRequestOptions { ConsistencyLevel = ConsistencyLevel.Strong },
                cancellationToken).ConfigureAwait(false)).Resource;
        }
        catch (CosmosException ce) when (IsMissingItem(ce))
        {
            if (versionEntity is null)
            {
                versionEntity = new ClusterVersionEntity
                {
                    ClusterId = _clusterId,
                    ClusterVersion = 0,
                    Id = CLUSTER_VERSION_ID
                };

                try
                {
                    var response = await _container.CreateItemAsync(versionEntity, _partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.Created)
                    {
                        LogDebugCreatedNewClusterVersionEntity();
                    }
                }
                catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
                {
                    // Another initializer created the version row.
                }
            }
        }
    }

    [Obsolete("Use DeleteMembershipTableEntriesAsync instead.")]
    public Task DeleteMembershipTableEntries(string clusterId) => DeleteMembershipTableEntriesAsync(clusterId, CancellationToken.None);

    public async Task DeleteMembershipTableEntriesAsync(string clusterId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(clusterId, _clusterId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The cluster ID must match this membership table's configured cluster ID '{_clusterId}'.",
                nameof(clusterId));
        }

        try
        {
            var snapshot = await ReadMembershipSnapshot(siloId: null, cancellationToken).ConfigureAwait(false);

            foreach (var chunk in snapshot.Members.Chunk(MaxBatchSize))
            {
                var batch = _container.CreateTransactionalBatch(_partitionKey);
                foreach (var silo in chunk)
                {
                    batch.DeleteItem(
                        ConstructSiloEntityId(silo.Item1.SiloAddress),
                        new TransactionalBatchItemRequestOptions { IfMatchEtag = silo.Item2 });
                }

                using var response = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw CreateBatchException(response);
                }
            }

            await _container.DeleteItemAsync<ClusterVersionEntity>(
                CLUSTER_VERSION_ID, _partitionKey,
                new ItemRequestOptions { IfMatchEtag = snapshot.Version.VersionEtag },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogErrorDeletingMembershipTableEntries(ex);
            WrappedException.CreateAndRethrow(ex);
        }
    }

    [Obsolete("Use CleanupDefunctSiloEntriesAsync instead.")]
    public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => CleanupDefunctSiloEntriesAsync(beforeDate, CancellationToken.None);

    public async Task CleanupDefunctSiloEntriesAsync(DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var deadSilos = await ReadSilos(cancellationToken, SiloStatus.Dead).ConfigureAwait(false);
            foreach (var silo in deadSilos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (GetEffectiveUpdateTime(silo) >= beforeDate.UtcDateTime)
                {
                    continue;
                }

                try
                {
                    await _container.DeleteItemAsync<SiloEntity>(
                        silo.Id, _partitionKey,
                        new ItemRequestOptions { IfMatchEtag = silo.ETag },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    // A concurrent update retains the row for a later cleanup pass.
                }
                catch (CosmosException exception) when (IsMissingItem(exception))
                {
                    await ReadClusterVersion(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogErrorCleaningUpDefunctSiloEntries(ex);
            WrappedException.CreateAndRethrow(ex);
        }
    }

    [Obsolete("Use ReadRowAsync instead.")]
    public Task<MembershipTableData> ReadRow(SiloAddress key) => ReadRowAsync(key, CancellationToken.None);

    public async Task<MembershipTableData> ReadRowAsync(SiloAddress key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = ConstructSiloEntityId(key);

        try
        {
            return await ReadMembershipSnapshot(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            LogWarningFailureReadingSiloEntry(exc, key, _clusterId);
            WrappedException.CreateAndRethrow(exc);
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
            return await ReadMembershipSnapshot(siloId: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            LogWarningReadingEntries(exc, _clusterId);
            WrappedException.CreateAndRethrow(exc);
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
            var siloEntity = ConvertToEntity(entry, _clusterId);
            var versionEntity = BuildVersionEntity(tableVersion);

            using var response = await _container.CreateTransactionalBatch(_partitionKey)
                .ReplaceItem(versionEntity.Id, versionEntity, new TransactionalBatchItemRequestOptions { IfMatchEtag = tableVersion.VersionEtag })
                .CreateItem(siloEntity)
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);

            return await IsSuccessfulMembershipBatch(response, allowMissingRow: false, cancellationToken).ConfigureAwait(false);
        }
        catch (CosmosException exc)
        {
            if (exc.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed) return false;
            WrappedException.CreateAndRethrow(exc);
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
            var siloEntity = ConvertToEntity(entry, _clusterId);
            siloEntity.ETag = etag;
            SiloEntity current;
            try
            {
                current = (await _container.ReadItemAsync<SiloEntity>(
                    siloEntity.Id, _partitionKey,
                    new ItemRequestOptions { ConsistencyLevel = ConsistencyLevel.Strong },
                    cancellationToken).ConfigureAwait(false)).Resource;
            }
            catch (CosmosException exception) when (IsMissingItem(exception))
            {
                await ReadClusterVersion(cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (!string.Equals(current.ETag, etag, StringComparison.Ordinal))
            {
                return false;
            }

            if (current.IAmAliveTime > siloEntity.IAmAliveTime)
            {
                siloEntity.IAmAliveTime = current.IAmAliveTime;
            }

            var versionEntity = BuildVersionEntity(tableVersion);

            using var response = await _container.CreateTransactionalBatch(_partitionKey)
                .ReplaceItem(versionEntity.Id, versionEntity, new TransactionalBatchItemRequestOptions { IfMatchEtag = tableVersion.VersionEtag })
                .ReplaceItem(siloEntity.Id, siloEntity, new TransactionalBatchItemRequestOptions { IfMatchEtag = siloEntity.ETag })
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);

            return await IsSuccessfulMembershipBatch(response, allowMissingRow: true, cancellationToken).ConfigureAwait(false);
        }
        catch (CosmosException exc)
        {
            if (exc.StatusCode == HttpStatusCode.PreconditionFailed) return false;
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    [Obsolete("Use UpdateIAmAliveAsync instead.")]
    public Task UpdateIAmAlive(MembershipEntry entry) => UpdateIAmAliveAsync(entry, CancellationToken.None);

    public async Task UpdateIAmAliveAsync(MembershipEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var siloEntityId = ConstructSiloEntityId(entry.SiloAddress);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = (await _container.ReadItemAsync<SiloEntity>(
                    siloEntityId, _partitionKey,
                    new ItemRequestOptions { ConsistencyLevel = ConsistencyLevel.Strong },
                    cancellationToken).ConfigureAwait(false)).Resource;
                if (current.IAmAliveTime.UtcDateTime >= entry.IAmAliveTime)
                {
                    return;
                }

                current.IAmAliveTime = entry.IAmAliveTime;
                try
                {
                    await _container.ReplaceItemAsync(
                        current, siloEntityId, _partitionKey,
                        new ItemRequestOptions { IfMatchEtag = current.ETag },
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    // Re-read after a concurrent heartbeat or membership update.
                }
            }
        }
        catch (CosmosException exception) when (IsMissingItem(exception))
        {
            // A surviving version row distinguishes a retired silo from missing membership resources.
            await ReadClusterVersion(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    private async Task<MembershipTableData> ReadMembershipSnapshot(string? siloId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxMembershipSnapshotAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = await ReadClusterVersion(cancellationToken).ConfigureAwait(false);
            var silos = new List<SiloEntity>();
            if (siloId is not null)
            {
                try
                {
                    var response = await _container.ReadItemAsync<SiloEntity>(
                        siloId, _partitionKey,
                        new ItemRequestOptions { ConsistencyLevel = ConsistencyLevel.Strong },
                        cancellationToken).ConfigureAwait(false);
                    silos.Add(response.Resource);
                }
                catch (CosmosException exception) when (IsMissingItem(exception))
                {
                    // The closing version read verifies the membership view containing this absence.
                }
            }
            else
            {
                string? continuationToken = null;
                do
                {
                    var queryOptions = new QueryRequestOptions
                    {
                        PartitionKey = _partitionKey,
                        ConsistencyLevel = ConsistencyLevel.Strong
                    };
                    // Heartbeats replace documents without changing the version fence; an immutable
                    // order keeps those replacements from moving rows across continuation pages.
                    using var iterator = _container.GetItemQueryIterator<SiloEntity>(
                        CreateSiloQuery(), continuationToken, queryOptions);
                    var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                    silos.AddRange(page);
                    continuationToken = page.ContinuationToken;
                } while (!string.IsNullOrEmpty(continuationToken));
            }

            // Strong reads can straddle a membership update; matching version etags fence the view.
            var after = await ReadClusterVersion(cancellationToken).ConfigureAwait(false);
            if (string.Equals(before.ETag, after.ETag, StringComparison.Ordinal))
            {
                return new MembershipTableData(
                    silos.Select(entity => Tuple.Create(ParseEntity(entity), entity.ETag!)).ToList(),
                    new TableVersion(after.Resource.ClusterVersion, after.ETag));
            }
        }

        throw new OrleansException(
            $"Unable to read a consistent membership snapshot for cluster '{_clusterId}' after {MaxMembershipSnapshotAttempts} attempts.");
    }

    private async Task<bool> IsSuccessfulMembershipBatch(
        TransactionalBatchResponse response, bool allowMissingRow, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        if (response.Any(result => result.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed))
        {
            return false;
        }

        if (allowMissingRow && response.Count == 2
            && response[0].StatusCode == HttpStatusCode.FailedDependency
            && response[1].StatusCode == HttpStatusCode.NotFound)
        {
            await ReadClusterVersion(cancellationToken).ConfigureAwait(false);
            return false;
        }

        throw CreateBatchException(response);
    }

    private static CosmosException CreateBatchException(TransactionalBatchResponse response)
        => new(response.ErrorMessage, response.StatusCode, 0, response.ActivityId, response.RequestCharge);

    private static bool IsMissingItem(CosmosException exception)
        => exception.StatusCode == HttpStatusCode.NotFound && exception.SubStatusCode == 0;

    private async Task InitializeCosmosClient(CancellationToken cancellationToken)
    {
        try
        {
            // A configured factory can return a shared client. Retain its task so retries reuse it.
            if (_clientTask is { IsCompleted: true, IsCompletedSuccessfully: false })
            {
                _clientTask = null;
            }

            var clientTask = _clientTask ??= _options.CreateClient!(_serviceProvider).AsTask();
            clientTask.Ignore();
            _client = await clientTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _clientTask = null;
            LogErrorInitializingCosmosClient(ex);
            WrappedException.CreateAndRethrow(ex);
            throw;
        }
    }

    private async Task TryDeleteDatabase(CancellationToken cancellationToken)
    {
        try
        {
            await _client.GetDatabase(_options.DatabaseName).DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (CosmosException dce) when (dce.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogErrorDeletingCosmosDBDatabase(ex);
            WrappedException.CreateAndRethrow(ex);
            throw;
        }
    }

    private async Task TryCreateCosmosResources(CancellationToken cancellationToken)
    {
        var dbResponse = await _client.CreateDatabaseIfNotExistsAsync(_options.DatabaseName, _options.DatabaseThroughput, cancellationToken: cancellationToken).ConfigureAwait(false);
        var db = dbResponse.Database;

        var containerProperties = new ContainerProperties(_options.ContainerName, PARTITION_KEY);
        containerProperties.IndexingPolicy.IndexingMode = IndexingMode.Consistent;
        containerProperties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/Address/?" });
        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/Port/?" });
        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/Generation/?" });
        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/Hostname/?" });
        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/SiloName/?" });
        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/\"SuspectingSilos\"/[]/?" });
        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/\"SuspectingTimes\"/[]/?" });
        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/StartTime/?" });
        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/IAmAliveTime/?" });

        const int maxRetries = 3;
        for (var retry = 0; retry <= maxRetries; ++retry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var containerResponse = await db.CreateContainerIfNotExistsAsync(
                containerProperties,
                _options.ContainerThroughputProperties,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (retry == maxRetries || dbResponse.StatusCode != HttpStatusCode.Created || containerResponse.StatusCode == HttpStatusCode.Created)
            {
                break;  // Apparently some throttling logic returns HttpStatusCode.OK (not 429) when the collection wasn't created in a new DB.
            }
            await Task.Delay(1000, cancellationToken);
        }
    }

    private async Task<ItemResponse<ClusterVersionEntity>> ReadClusterVersion(CancellationToken cancellationToken)
    {
        try
        {
            return await _container.ReadItemAsync<ClusterVersionEntity>(
                CLUSTER_VERSION_ID,
                _partitionKey,
                new ItemRequestOptions { ConsistencyLevel = ConsistencyLevel.Strong },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogErrorReadingClusterVersionEntity(ex);
            WrappedException.CreateAndRethrow(ex);
            throw;
        }
    }

    private async Task<IReadOnlyList<SiloEntity>> ReadSilos(CancellationToken cancellationToken, SiloStatus? status = null)
    {
        try
        {
            using var iterator = _container.GetItemQueryIterator<SiloEntity>(
                CreateSiloQuery(status),
                requestOptions: new QueryRequestOptions { PartitionKey = _partitionKey, ConsistencyLevel = ConsistencyLevel.Strong });

            var silos = new List<SiloEntity>();
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var items = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                silos.AddRange(items);
            } while (iterator.HasMoreResults);

            return silos;
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            LogErrorReadingSiloEntities(exc);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    private static string ConstructSiloEntityId(SiloAddress silo) => $"{silo.Endpoint.Address}-{silo.Endpoint.Port}-{silo.Generation}";

    private static QueryDefinition CreateSiloQuery(SiloStatus? status = null)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.EntityType = @entityType"
            + (status.HasValue ? " AND c.Status = @status" : "")
            + " ORDER BY c.id")
            .WithParameter("@entityType", nameof(SiloEntity));
        if (status.HasValue)
        {
            query.WithParameter("@status", (int)status.Value);
        }

        return query;
    }

    private static DateTime GetEffectiveUpdateTime(SiloEntity entity)
    {
        var result = entity.StartTime > entity.IAmAliveTime ? entity.StartTime.UtcDateTime : entity.IAmAliveTime.UtcDateTime;
        foreach (var value in entity.SuspectingTimes)
        {
            var suspectTime = LogFormatter.ParseDate(value);
            result = suspectTime > result ? suspectTime : result;
        }

        return result;
    }

    private static MembershipEntry ParseEntity(SiloEntity entity)
    {
        var entry = new MembershipEntry
        {
            HostName = entity.Hostname,
            Status = (SiloStatus)entity.Status
        };

        if (entity.ProxyPort.HasValue)
            entry.ProxyPort = entity.ProxyPort.Value;

        entry.SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Parse(entity.Address), entity.Port), entity.Generation);

        entry.SiloName = entity.SiloName;

        entry.StartTime = entity.StartTime.UtcDateTime;

        entry.IAmAliveTime = entity.IAmAliveTime.UtcDateTime;

        var suspectingSilos = new List<SiloAddress>();
        var suspectingTimes = new List<DateTime>();

        foreach (var silo in entity.SuspectingSilos)
        {
            suspectingSilos.Add(SiloAddress.FromParsableString(silo));
        }

        foreach (var time in entity.SuspectingTimes)
        {
            suspectingTimes.Add(LogFormatter.ParseDate(time));
        }

        if (suspectingSilos.Count != suspectingTimes.Count)
        {
            throw new OrleansException($"SuspectingSilos.Length of {suspectingSilos.Count} as read from Azure Cosmos DB is not equal to SuspectingTimes.Length of {suspectingTimes.Count}");
        }

        for (var i = 0; i < suspectingSilos.Count; i++)
        {
            entry.AddSuspector(suspectingSilos[i], suspectingTimes[i]);
        }

        return entry;
    }

    private static SiloEntity ConvertToEntity(MembershipEntry memEntry, string clusterId)
    {
        var tableEntry = new SiloEntity
        {
            Id = ConstructSiloEntityId(memEntry.SiloAddress),
            ClusterId = clusterId,
            Address = memEntry.SiloAddress.Endpoint.Address.ToString(),
            Port = memEntry.SiloAddress.Endpoint.Port,
            Generation = memEntry.SiloAddress.Generation,
            Hostname = memEntry.HostName,
            Status = (int)memEntry.Status,
            ProxyPort = memEntry.ProxyPort,
            SiloName = memEntry.SiloName,
            StartTime = memEntry.StartTime,
            IAmAliveTime = memEntry.IAmAliveTime
        };

        if (memEntry.SuspectTimes != null)
        {
            foreach (var tuple in memEntry.SuspectTimes)
            {
                tableEntry.SuspectingSilos.Add(tuple.Item1.ToParsableString());
                tableEntry.SuspectingTimes.Add(LogFormatter.PrintDate(tuple.Item2));
            }
        }

        return tableEntry;
    }

    private ClusterVersionEntity BuildVersionEntity(TableVersion tableVersion)
    {
        return new ClusterVersionEntity
        {
            ClusterId = _clusterId,
            ClusterVersion = tableVersion.Version,
            Id = CLUSTER_VERSION_ID,
            ETag = tableVersion.VersionEtag
        };
    }

    private readonly struct MembershipEntryLogValue(MembershipEntry membershipEntry)
    {
        public override string ToString() => membershipEntry.ToFullString();
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Created new Cluster Version entity."
    )]
    private partial void LogDebugCreatedNewClusterVersionEntity();

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error deleting membership table entries."
    )]
    private partial void LogErrorDeletingMembershipTableEntries(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error cleaning up defunct silo entries."
    )]
    private partial void LogErrorCleaningUpDefunctSiloEntries(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failure reading silo entry {Key} for cluster {Cluster}"
    )]
    private partial void LogWarningFailureReadingSiloEntry(Exception exception, SiloAddress key, string cluster);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Initial ClusterVersionEntity entity does not exist."
    )]
    private partial void LogErrorClusterVersionEntityDoesNotExist();

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failure reading all membership records."
    )]
    private partial void LogErrorReadingAllMembershipRecords(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failure reading entries for cluster {Cluster}"
    )]
    private partial void LogWarningReadingEntries(Exception exception, string cluster);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error initializing Azure Cosmos DB Client for membership table provider."
    )]
    private partial void LogErrorInitializingCosmosClient(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error deleting Azure Cosmos DB database."
    )]
    private partial void LogErrorDeletingCosmosDBDatabase(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error reading Cluster Version entity."
    )]
    private partial void LogErrorReadingClusterVersionEntity(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error reading Silo entities."
    )]
    private partial void LogErrorReadingSiloEntities(Exception exception);

    [LoggerMessage(
        EventId = (int)ErrorCode.MembershipBase,
        Level = LogLevel.Warning,
        Message = "Unable to query entry {Entry}"
    )]
    private partial void LogWarningUnableToQueryEntry(MembershipEntryLogValue entry);
}
