using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Immutable;
using Orleans.Clustering.Cosmos.Models;

namespace Orleans.Clustering.Cosmos;

internal partial class CosmosMembershipTable : IMembershipTable
{
    private const string PARTITION_KEY = "/ClusterId";
    private const string CLUSTER_VERSION_ID = "ClusterVersion";
    internal static readonly TimeSpan MetadataOrphanGracePeriod = TimeSpan.FromMinutes(5);
    private readonly ILogger _logger;
    private readonly CosmosClusteringOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _clusterId;
    private readonly PartitionKey _partitionKey;
    private readonly QueryRequestOptions _queryRequestOptions;
    private CosmosClient _client = default!;
    private Container _container = default!;
    private Container _metadataContainer = default!;
    private string _metadataContainerName = default!;
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
        _metadataContainerName = GetMetadataContainerName(_options);
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
        _metadataContainer = _client.GetContainer(_options.DatabaseName, _metadataContainerName);
        await ValidateContainers().ConfigureAwait(false);

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

                if (response.StatusCode == HttpStatusCode.Created)
                {
                    LogDebugCreatedNewClusterVersionEntity();
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

            var response = await batch.ExecuteAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new OrleansException($"Unable to delete Cosmos DB membership entries. Status code: {response.StatusCode}.");
            }

            await DeleteMetadata((await ReadMetadata().ConfigureAwait(false)).Keys).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogErrorDeletingMembershipTableEntries(ex);
            WrappedException.CreateAndRethrow(ex);
        }
    }

    public async Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
    {
        try
        {
            // Filter by status server-side (Status is indexed); apply the date check in C#
            // so that the Math.Max(IAmAliveTime, StartTime) semantics are preserved correctly.
            var activeStatus = (int)SiloStatus.Active;
            var query = _container
                .GetItemLinqQueryable<SiloEntity>(requestOptions: _queryRequestOptions)
                .Where(g => g.EntityType == nameof(SiloEntity) && g.Status != activeStatus);

            var iterator = query.ToFeedIterator();
            var nonActiveSilos = new List<SiloEntity>();
            do
            {
                var items = await iterator.ReadNextAsync().ConfigureAwait(false);
                nonActiveSilos.AddRange(items);
            } while (iterator.HasMoreResults);

            var silos = nonActiveSilos
                .Where(s => Math.Max(s.IAmAliveTime.Ticks, s.StartTime.Ticks) < beforeDate.Ticks)
                .ToList();

            if (silos.Count > 0)
            {
                var batch = _container.CreateTransactionalBatch(_partitionKey);

                foreach (var silo in silos)
                {
                    batch = batch.DeleteItem(silo.Id);
                }

                var response = await batch.ExecuteAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new OrleansException($"Unable to clean up defunct Cosmos DB membership entries. Status code: {response.StatusCode}.");
                }

                await DeleteMetadata(silos.Select(silo => silo.Id)).ConfigureAwait(false);
            }

            await DeleteOrphanedMetadata().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogErrorCleaningUpDefunctSiloEntries(ex);
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
            var readMetadataTask = ReadMetadata(id);

            await Task.WhenAll(readClusterVersionTask, readSiloTask, readMetadataTask).ConfigureAwait(false);

            var clusterVersion = await readClusterVersionTask;
            var silo = await readSiloTask;
            var metadata = await readMetadataTask;

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
                Tuple.Create(ParseEntity(silo.Resource, metadata), silo.Resource.ETag!)
            };

            // A cluster version record is created during provider initialization.
            return new MembershipTableData(memEntries, version!);
        }
        catch (Exception exc)
        {
            LogWarningFailureReadingSiloEntry(exc, key, _clusterId);
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
            var readMetadataTask = ReadMetadata();

            await Task.WhenAll(readClusterVersionTask, readSilosTask, readMetadataTask).ConfigureAwait(false);

            var clusterVersion = await readClusterVersionTask;
            var silos = await readSilosTask;
            var metadata = await readMetadataTask;

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
                    metadata.TryGetValue(entity.Id, out var siloMetadata);
                    var membershipEntry = ParseEntity(entity, siloMetadata);
                    // Cosmos populates ETag on resources returned from reads.
                    memEntries.Add(new Tuple<MembershipEntry, string>(membershipEntry, entity.ETag!));
                }
                catch (Exception exc)
                {
                    LogErrorReadingAllMembershipRecords(exc);
                    WrappedException.CreateAndRethrow(exc);
                    throw;
                }
            }

            // A cluster version record is created during provider initialization.
            return new MembershipTableData(memEntries, version!);
        }
        catch (Exception exc)
        {
            LogWarningReadingEntries(exc, _clusterId);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
    {
        try
        {
            var siloEntity = ConvertToEntity(entry, _clusterId);
            siloEntity.Metadata = await EnsureMetadata(siloEntity.Id, siloEntity.Metadata).ConfigureAwait(false) ?? siloEntity.Metadata;
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
            siloEntity.Metadata = await EnsureMetadata(
                siloEntity.Id,
                siloEntity.Metadata,
                preserveInlineMetadata: true).ConfigureAwait(false) ?? siloEntity.Metadata;
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
            LogErrorInitializingCosmosClient(ex);
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
            LogErrorDeletingCosmosDBDatabase(ex);
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
            var containerResponse = await db.CreateContainerIfNotExistsAsync(
                containerProperties,
                _options.ContainerThroughputProperties).ConfigureAwait(false);

            if (retry == maxRetries || dbResponse.StatusCode != HttpStatusCode.Created || containerResponse.StatusCode == HttpStatusCode.Created)
            {
                break;  // Apparently some throttling logic returns HttpStatusCode.OK (not 429) when the collection wasn't created in a new DB.
            }
            await Task.Delay(1000);
        }

        var metadataContainerProperties = new ContainerProperties(_metadataContainerName, PARTITION_KEY);
        metadataContainerProperties.IndexingPolicy.IndexingMode = IndexingMode.Consistent;
        metadataContainerProperties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/ClusterId/?" });
        metadataContainerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/*" });
        await db.CreateContainerIfNotExistsAsync(
            metadataContainerProperties,
            _options.ContainerThroughputProperties).ConfigureAwait(false);
    }

    internal static string GetMetadataContainerName(CosmosClusteringOptions options)
    {
        string result;
        if (!string.IsNullOrWhiteSpace(options.MetadataContainerName))
        {
            result = options.MetadataContainerName;
        }
        else
        {
            const string suffix = "-Metadata";
            if (options.ContainerName.Length + suffix.Length <= 255)
            {
                result = options.ContainerName + suffix;
            }
            else
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.ContainerName)));
                result = options.ContainerName[..229] + suffix + "-" + hash[..16];
            }
        }

        if (string.Equals(result, options.ContainerName, StringComparison.Ordinal))
        {
            throw new OrleansConfigurationException(
                $"{nameof(CosmosClusteringOptions.MetadataContainerName)} must differ from {nameof(CosmosOptions.ContainerName)}.");
        }

        return result;
    }

    private async Task ValidateContainers()
    {
        try
        {
            await _container.ReadContainerAsync().ConfigureAwait(false);
            var metadataContainer = await _metadataContainer.ReadContainerAsync().ConfigureAwait(false);
            if (!string.Equals(metadataContainer.Resource.PartitionKeyPath, PARTITION_KEY, StringComparison.Ordinal))
            {
                throw new OrleansConfigurationException(
                    $"Cosmos DB companion metadata container '{_metadataContainerName}' must use partition key path '{PARTITION_KEY}', "
                    + $"but uses '{metadataContainer.Resource.PartitionKeyPath}'.");
            }
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            throw new OrleansException(
                $"Cosmos DB membership requires both container '{_options.ContainerName}' and companion metadata container '{_metadataContainerName}' in database '{_options.DatabaseName}'. "
                + $"Enable {nameof(CosmosOptions.IsResourceCreationEnabled)} or provision both containers with partition key '{PARTITION_KEY}'.",
                exception);
        }
    }

    private async Task<Dictionary<string, string>?> EnsureMetadata(
        string id,
        Dictionary<string, string>? metadata,
        bool preserveInlineMetadata = false)
    {
        if (preserveInlineMetadata)
        {
            var existingMetadata = await ReadMetadata(id).ConfigureAwait(false);
            if (existingMetadata is not null)
            {
                return existingMetadata;
            }

            try
            {
                var existingMembership = await _container.ReadItemAsync<SiloEntity>(id, _partitionKey).ConfigureAwait(false);
                metadata = existingMembership.Resource.Metadata ?? metadata;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
            }
        }

        if (metadata is not null)
        {
            try
            {
                var entity = new SiloMetadataEntity
                {
                    Id = id,
                    ClusterId = _clusterId,
                    Metadata = metadata,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                await _metadataContainer.CreateItemAsync(entity, _partitionKey).ConfigureAwait(false);
                return metadata;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
            {
                var existing = await ReadMetadataEntity(id).ConfigureAwait(false);
                if (existing is not null)
                {
                    existing.CreatedAt = DateTimeOffset.UtcNow;
                    try
                    {
                        var response = await _metadataContainer.ReplaceItemAsync(
                            existing,
                            id,
                            _partitionKey,
                            new ItemRequestOptions { IfMatchEtag = existing.ETag }).ConfigureAwait(false);
                        return response.Resource.Metadata;
                    }
                    catch (CosmosException updateException) when (updateException.StatusCode == HttpStatusCode.PreconditionFailed)
                    {
                        return (await ReadMetadataEntity(id).ConfigureAwait(false))?.Metadata;
                    }
                }
            }
        }

        return await ReadMetadata(id).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, string>?> ReadMetadata(string id)
        => (await ReadMetadataEntity(id).ConfigureAwait(false))?.Metadata;

    private async Task<SiloMetadataEntity?> ReadMetadataEntity(string id)
    {
        try
        {
            var response = await _metadataContainer.ReadItemAsync<SiloMetadataEntity>(id, _partitionKey).ConfigureAwait(false);
            return response.Resource;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<Dictionary<string, Dictionary<string, string>>> ReadMetadata()
    {
        var query = _metadataContainer
            .GetItemLinqQueryable<SiloMetadataEntity>(requestOptions: _queryRequestOptions)
            .Where(entity => entity.ClusterId == _clusterId);
        var iterator = query.ToFeedIterator();
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        do
        {
            var items = await iterator.ReadNextAsync().ConfigureAwait(false);
            foreach (var item in items)
            {
                result[item.Id] = item.Metadata;
            }
        } while (iterator.HasMoreResults);

        return result;
    }

    private async Task DeleteMetadata(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            try
            {
                await _metadataContainer.DeleteItemAsync<SiloMetadataEntity>(id, _partitionKey).ConfigureAwait(false);
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
            }
        }
    }

    private async Task DeleteOrphanedMetadata()
    {
        var membershipIds = (await ReadSilos().ConfigureAwait(false))
            .Select(silo => silo.Id)
            .ToHashSet(StringComparer.Ordinal);
        var cutoff = DateTimeOffset.UtcNow - MetadataOrphanGracePeriod;
        var query = _metadataContainer
            .GetItemLinqQueryable<SiloMetadataEntity>(requestOptions: _queryRequestOptions)
            .Where(entity => entity.ClusterId == _clusterId);
        var iterator = query.ToFeedIterator();
        do
        {
            var items = await iterator.ReadNextAsync().ConfigureAwait(false);
            foreach (var item in items.Where(item => item.CreatedAt <= cutoff && !membershipIds.Contains(item.Id)))
            {
                try
                {
                    await _metadataContainer.DeleteItemAsync<SiloMetadataEntity>(
                        item.Id,
                        _partitionKey,
                        new ItemRequestOptions { IfMatchEtag = item.ETag }).ConfigureAwait(false);
                }
                catch (CosmosException exception) when (
                    exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
                {
                }
            }
        } while (iterator.HasMoreResults);
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
            LogErrorReadingClusterVersionEntity(ex);
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
            LogErrorReadingSiloEntities(exc);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    private static string ConstructSiloEntityId(SiloAddress silo) => $"{silo.Endpoint.Address}-{silo.Endpoint.Port}-{silo.Generation}";

    private static MembershipEntry ParseEntity(SiloEntity entity, Dictionary<string, string>? companionMetadata)
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

        entry.Metadata = (companionMetadata ?? entity.Metadata)?.ToImmutableDictionary();

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
            IAmAliveTime = memEntry.IAmAliveTime,
            Metadata = memEntry.Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
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
