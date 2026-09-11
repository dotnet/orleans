using System.Net;
using System.Threading;
using Orleans.Clustering.Cosmos.Models;

namespace Orleans.Clustering.Cosmos;

internal partial class CosmosMembershipTable : IMembershipTable
{
    private const string PARTITION_KEY = "/ClusterId";
    private const string CLUSTER_VERSION_ID = "ClusterVersion";
    private readonly ILogger _logger;
    private readonly CosmosClusteringOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _clusterId;
    private readonly PartitionKey _partitionKey;
    private readonly QueryRequestOptions _queryRequestOptions;
    private Task<CosmosClient>? _clientTask;
    private CosmosClient _client = default!;
    private Container _container = default!;
    private SiloEntity? _self = null;

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

        _queryRequestOptions = new() { PartitionKey = _partitionKey };
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
            versionEntity = (await _container.ReadItemAsync<ClusterVersionEntity>(CLUSTER_VERSION_ID, _partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false)).Resource;
        }
        catch (CosmosException ce) when (ce.StatusCode == HttpStatusCode.NotFound)
        {
            if (versionEntity is null)
            {
                versionEntity = new ClusterVersionEntity
                {
                    ClusterId = _clusterId,
                    ClusterVersion = 0,
                    Id = CLUSTER_VERSION_ID
                };

                var response = await _container.CreateItemAsync(versionEntity, _partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Created)
                {
                    LogDebugCreatedNewClusterVersionEntity();
                }
            }
        }
    }

    [Obsolete("Use DeleteMembershipTableEntriesAsync instead.")]
    public Task DeleteMembershipTableEntries(string clusterId) => DeleteMembershipTableEntriesAsync(clusterId, CancellationToken.None);

    public async Task DeleteMembershipTableEntriesAsync(string clusterId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var silos = await ReadSilos(cancellationToken).ConfigureAwait(false);

            var batch = _container.CreateTransactionalBatch(_partitionKey);

            foreach (var silo in silos)
            {
                batch = batch.DeleteItem(silo.Id);
            }

            batch = batch.DeleteItem(CLUSTER_VERSION_ID);

            using var response = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
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
            // Filter by status server-side (Status is indexed); apply the date check in C#
            // so that the Math.Max(IAmAliveTime, StartTime) semantics are preserved correctly.
            var activeStatus = (int)SiloStatus.Active;
            var query = _container
                .GetItemLinqQueryable<SiloEntity>(requestOptions: _queryRequestOptions)
                .Where(g => g.EntityType == nameof(SiloEntity) && g.Status != activeStatus);

            using var iterator = query.ToFeedIterator();
            var nonActiveSilos = new List<SiloEntity>();
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var items = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                nonActiveSilos.AddRange(items);
            } while (iterator.HasMoreResults);

            var silos = nonActiveSilos
                .Where(s => Math.Max(s.IAmAliveTime.Ticks, s.StartTime.Ticks) < beforeDate.Ticks)
                .ToList();

            if (silos.Count == 0)
            {
                return;
            }

            var batch = _container.CreateTransactionalBatch(_partitionKey);

            foreach (var silo in silos)
            {
                batch = batch.DeleteItem(silo.Id);
            }

            using var response = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
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
            var readClusterVersionTask = ReadClusterVersion(cancellationToken);
            var readSiloTask = _container.ReadItemAsync<SiloEntity>(id, _partitionKey, cancellationToken: cancellationToken);

            await Task.WhenAll(readClusterVersionTask, readSiloTask).ConfigureAwait(false);

            var clusterVersion = await readClusterVersionTask;
            var silo = await readSiloTask;

            TableVersion? version = null;
            if (clusterVersion is not null)
            {
                // Cosmos populates ETag on resources returned from reads.
                version = new TableVersion(clusterVersion.ClusterVersion, clusterVersion.ETag!);
            }
            else
            {
                LogErrorClusterVersionEntityDoesNotExist();
            }

            var memEntries = new List<Tuple<MembershipEntry, string>>
            {
                // Cosmos populates ETag on resources returned from reads.
                Tuple.Create(ParseEntity(silo.Resource), silo.Resource.ETag!)
            };

            // A cluster version record is created during provider initialization.
            return new MembershipTableData(memEntries, version!);
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
            var readClusterVersionTask = ReadClusterVersion(cancellationToken);
            var readSilosTask = ReadSilos(cancellationToken);

            await Task.WhenAll(readClusterVersionTask, readSilosTask).ConfigureAwait(false);

            var clusterVersion = await readClusterVersionTask;
            var silos = await readSilosTask;

            TableVersion? version = null;
            if (clusterVersion is not null)
            {
                // Cosmos populates ETag on resources returned from reads.
                version = new TableVersion(clusterVersion.ClusterVersion, clusterVersion.ETag!);
            }
            else
            {
                LogErrorClusterVersionEntityDoesNotExist();
            }

            var memEntries = new List<Tuple<MembershipEntry, string>>();
            foreach (var entity in silos)
            {
                try
                {
                    var membershipEntry = ParseEntity(entity);
                    // Cosmos populates ETag on resources returned from reads.
                    memEntries.Add(new Tuple<MembershipEntry, string>(membershipEntry, entity.ETag!));
                }
                catch (Exception exc) when (exc is not OperationCanceledException)
                {
                    LogErrorReadingAllMembershipRecords(exc);
                    WrappedException.CreateAndRethrow(exc);
                    throw;
                }
            }

            // A cluster version record is created during provider initialization.
            return new MembershipTableData(memEntries, version!);
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

            return response.IsSuccessStatusCode;
        }
        catch (CosmosException exc)
        {
            if (exc.StatusCode == HttpStatusCode.PreconditionFailed) return false;
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

            var versionEntity = BuildVersionEntity(tableVersion);

            using var response = await _container.CreateTransactionalBatch(_partitionKey)
                .ReplaceItem(versionEntity.Id, versionEntity, new TransactionalBatchItemRequestOptions { IfMatchEtag = tableVersion.VersionEtag })
                .ReplaceItem(siloEntity.Id, siloEntity, new TransactionalBatchItemRequestOptions { IfMatchEtag = siloEntity.ETag })
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode;
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

        if (_self is not { } selfRow)
        {
            var response = await _container.ReadItemAsync<SiloEntity>(siloEntityId, _partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                LogWarningUnableToQueryEntry(new(entry));
                throw new OrleansException($"Unable to query for SiloEntity {entry.ToFullString()}");
            }

            _self = selfRow = response.Resource;
        }

        selfRow.IAmAliveTime = entry.IAmAliveTime;

        try
        {
            var replaceResponse = await _container.ReplaceItemAsync(
                selfRow,
                siloEntityId,
                _partitionKey,
                new ItemRequestOptions { IfMatchEtag = selfRow.ETag },
                cancellationToken).ConfigureAwait(false);
            _self = replaceResponse.Resource;
        }
        catch (OperationCanceledException)
        {
            _self = null;
            throw;
        }
        catch (Exception exc)
        {
            _self = null;
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

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

    private async Task<ClusterVersionEntity?> ReadClusterVersion(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _container.ReadItemAsync<ClusterVersionEntity>(
                CLUSTER_VERSION_ID,
                _partitionKey,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return response.StatusCode == HttpStatusCode.OK
                ? response.Resource
                : response.StatusCode == HttpStatusCode.NotFound
                    ? null
                    : throw new Exception($"Error reading Cluster Version entity. Status code: {response.StatusCode}");
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
            var query = _container
                .GetItemLinqQueryable<SiloEntity>(requestOptions: _queryRequestOptions)
                .Where(g => g.EntityType == nameof(SiloEntity));

            if (status is not null)
            {
                query = query.Where(g => (SiloStatus)g.Status == status);
            }

            using var iterator = query.ToFeedIterator();

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
