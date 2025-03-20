using System.Net;
using Orleans.Clustering.Cosmos.Models;

namespace Orleans.Clustering.Cosmos;

internal class CosmosMembershipTable : IMembershipTable
{
    private const string PARTITION_KEY = "/ClusterId";
    private const string CLUSTER_VERSION_ID = "ClusterVersion";
    private readonly ILogger _logger;
    private readonly CosmosClusteringOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _clusterId;
    private readonly PartitionKey _partitionKey;
    private readonly QueryRequestOptions _queryRequestOptions;
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

    public async Task InitializeMembershipTable(bool tryInitTableVersion)
    {
        await InitializeCosmosClient().ConfigureAwait(false);

        if (_options.IsResourceCreationEnabled)
        {
            if (_options.CleanResourcesOnInitialization)
            {
                await TryDeleteDatabase().ConfigureAwait(false);
            }

            await TryCreateCosmosResources().ConfigureAwait(false);
        }

        _container = _client.GetContainer(_options.DatabaseName, _options.ContainerName);

        ClusterVersionEntity? versionEntity = null;

        try
        {
            versionEntity = (await _container.ReadItemAsync<ClusterVersionEntity>(CLUSTER_VERSION_ID, _partitionKey).ConfigureAwait(false)).Resource;
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

                var response = await _container.CreateItemAsync(versionEntity, _partitionKey).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Created && _logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Created new Cluster Version entity.");
                }
            }
        }
    }

    public async Task DeleteMembershipTableEntries(string clusterId)
    {
        try
        {
            var silos = await ReadSilos().ConfigureAwait(false);

            var batch = _container.CreateTransactionalBatch(_partitionKey);

            foreach (var silo in silos)
            {
                batch = batch.DeleteItem(silo.Id);
            }

            batch = batch.DeleteItem(CLUSTER_VERSION_ID);

            await batch.ExecuteAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting membership table entries.");
            WrappedException.CreateAndRethrow(ex);
        }
    }

    public async Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
    {
        try
        {
            var silos = (await ReadSilos(SiloStatus.Dead).ConfigureAwait(false)).Where(s => s.IAmAliveTime < beforeDate).ToList();
            if (silos.Count == 0)
            {
                return;
            }

            var batch = _container.CreateTransactionalBatch(_partitionKey);

            foreach (var silo in silos)
            {
                batch = batch.DeleteItem(silo.Id);
            }

            await batch.ExecuteAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up defunct silo entries.");
            WrappedException.CreateAndRethrow(ex);
        }
    }

    public async Task<MembershipTableData> ReadRow(SiloAddress key)
    {
        var id = ConstructSiloEntityId(key);

        try
        {
            var readClusterVersionTask = ReadClusterVersion();
            var readSiloTask = _container.ReadItemAsync<SiloEntity>(id, _partitionKey);

            await Task.WhenAll(readClusterVersionTask, readSiloTask).ConfigureAwait(false);

            var clusterVersion = await readClusterVersionTask;
            var silo = await readSiloTask;

            TableVersion? version = null;
            if (clusterVersion is not null)
            {
                version = new TableVersion(clusterVersion.ClusterVersion, clusterVersion.ETag);
            }
            else
            {
                _logger.LogError("Initial ClusterVersionEntity entity does not exist.");
            }

            var memEntries = new List<Tuple<MembershipEntry, string>>
            {
                Tuple.Create(ParseEntity(silo.Resource), silo.Resource.ETag)
            };

            return new MembershipTableData(memEntries, version);
        }
        catch (Exception exc)
        {
            _logger.LogWarning(exc, "Failure reading silo entry {Key} for cluster {Cluster}", key, _clusterId);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task<MembershipTableData> ReadAll()
    {
        try
        {
            var readClusterVersionTask = ReadClusterVersion();
            var readSilosTask = ReadSilos();

            await Task.WhenAll(readClusterVersionTask, readSilosTask).ConfigureAwait(false);

            var clusterVersion = await readClusterVersionTask;
            var silos = await readSilosTask;

            TableVersion? version = null;
            if (clusterVersion is not null)
            {
                version = new TableVersion(clusterVersion.ClusterVersion, clusterVersion.ETag);
            }
            else
            {
                _logger.LogError("Initial ClusterVersionEntity entity does not exist.");
            }

            var memEntries = new List<Tuple<MembershipEntry, string>>();
            foreach (var entity in silos)
            {
                try
                {
                    var membershipEntry = ParseEntity(entity);
                    memEntries.Add(new Tuple<MembershipEntry, string>(membershipEntry, entity.ETag));
                }
                catch (Exception exc)
                {
                    _logger.LogError(exc, "Failure reading all membership records.");
                    WrappedException.CreateAndRethrow(exc);
                    throw;
                }
            }

            return new MembershipTableData(memEntries, version);
        }
        catch (Exception exc)
        {
            _logger.LogWarning(exc, "Failure reading entries for cluster {Cluster}", _clusterId);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
    {
        try
        {
            var siloEntity = ConvertToEntity(entry, _clusterId);
            var versionEntity = BuildVersionEntity(tableVersion);

            var response = await _container.CreateTransactionalBatch(_partitionKey)
                .ReplaceItem(versionEntity.Id, versionEntity, new TransactionalBatchItemRequestOptions { IfMatchEtag = tableVersion.VersionEtag })
                .CreateItem(siloEntity)
                .ExecuteAsync().ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (CosmosException exc)
        {
            if (exc.StatusCode == HttpStatusCode.PreconditionFailed) return false;
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
    {
        try
        {
            var siloEntity = ConvertToEntity(entry, _clusterId);
            siloEntity.ETag = etag;

            var versionEntity = BuildVersionEntity(tableVersion);

            var response = await _container.CreateTransactionalBatch(_partitionKey)
                .ReplaceItem(versionEntity.Id, versionEntity, new TransactionalBatchItemRequestOptions { IfMatchEtag = tableVersion.VersionEtag })
                .ReplaceItem(siloEntity.Id, siloEntity, new TransactionalBatchItemRequestOptions { IfMatchEtag = siloEntity.ETag })
                .ExecuteAsync().ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (CosmosException exc)
        {
            if (exc.StatusCode == HttpStatusCode.PreconditionFailed) return false;
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task UpdateIAmAlive(MembershipEntry entry)
    {
        var siloEntityId = ConstructSiloEntityId(entry.SiloAddress);

        if (_self is not { } selfRow)
        {
            var response = await _container.ReadItemAsync<SiloEntity>(siloEntityId, _partitionKey).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogWarning((int)ErrorCode.MembershipBase, "Unable to query entry {Entry}", entry.ToFullString());
                throw new OrleansException((string?)$"Unable to query for SiloEntity {entry.ToFullString()}");
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
                new ItemRequestOptions { IfMatchEtag = selfRow.ETag }).ConfigureAwait(false);
            _self = replaceResponse.Resource;
        }
        catch (Exception exc)
        {
            _self = null;
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    private async Task InitializeCosmosClient()
    {
        try
        {
            _client = await _options.CreateClient!(_serviceProvider).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing Azure Cosmos DB Client for membership table provider.");
            WrappedException.CreateAndRethrow(ex);
            throw;
        }
    }

    private async Task TryDeleteDatabase()
    {
        try
        {
            await _client.GetDatabase(_options.DatabaseName).DeleteAsync().ConfigureAwait(false);
        }
        catch (CosmosException dce) when (dce.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting Azure Cosmos DB database.");
            WrappedException.CreateAndRethrow(ex);
            throw;
        }
    }

    private async Task TryCreateCosmosResources()
    {
        var dbResponse = await _client.CreateDatabaseIfNotExistsAsync(_options.DatabaseName, _options.DatabaseThroughput).ConfigureAwait(false);
        var db = dbResponse.Database;

        var containerProperties = new ContainerProperties(_options.ContainerName, PARTITION_KEY);
        containerProperties.IndexingPolicy.IndexingMode = IndexingMode.Consistent;
        containerProperties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/Address/*" });
        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/Port/*" });
        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/Generation/*" });
        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/Hostname/*" });
        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/SiloName/*" });
        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/\"SuspectingSilos\"/*" });
        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/\"SuspectingTimes\"/*" });
        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/StartTime/*" });
        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/IAmAliveTime/*" });

        const int maxRetries = 3;
        for (var retry = 0; retry <= maxRetries; ++retry)
        {
            var containerResponse = await db.CreateContainerIfNotExistsAsync(
                containerProperties,
                _options.ContainerThroughputProperties).ConfigureAwait(false);

            if (retry == maxRetries || dbResponse.StatusCode != HttpStatusCode.Created || containerResponse.StatusCode == HttpStatusCode.Created)
            {
                break;  // Apparently some throttling logic returns HttpStatusCode.OK (not 429) when the collection wasn't created in a new DB.
            }
            await Task.Delay(1000);
        }
    }

    private async Task<ClusterVersionEntity?> ReadClusterVersion()
    {
        try
        {
            var response = await _container.ReadItemAsync<ClusterVersionEntity>(
                CLUSTER_VERSION_ID,
                _partitionKey).ConfigureAwait(false);

            return response.StatusCode == HttpStatusCode.OK
                ? response.Resource
                : response.StatusCode == HttpStatusCode.NotFound
                    ? null
                    : throw new Exception($"Error reading Cluster Version entity. Status code: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading Cluster Version entity.");
            WrappedException.CreateAndRethrow(ex);
            throw;
        }
    }

    private async Task<IReadOnlyList<SiloEntity>> ReadSilos(SiloStatus? status = null)
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

            var iterator = query.ToFeedIterator();

            var silos = new List<SiloEntity>();
            do
            {
                var items = await iterator.ReadNextAsync().ConfigureAwait(false);
                silos.AddRange(items);
            } while (iterator.HasMoreResults);

            return silos;
        }
        catch (Exception exc)
        {
            _logger.LogError(exc, "Error reading Silo entities.");
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
}