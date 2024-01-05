using System.Net;
using System.Threading;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;
using static Orleans.Persistence.Cosmos.CosmosIdSanitizer;

namespace Orleans.Persistence.Cosmos;

public class CosmosGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
{
    private const string ANY_ETAG = "*";
    private const string KEY_STRING_SEPARATOR = "__";
    private const string GRAINTYPE_PARTITION_KEY_PATH = "/GrainType";
    private readonly ILogger _logger;
    private readonly CosmosGrainStorageOptions _options;
    private readonly string _name;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _serviceId;
    private string _partitionKeyPath;
    private readonly IPartitionKeyProvider _partitionKeyProvider;
    private readonly ICosmosOperationExecutor _executor;
    private CosmosClient _client = default!;
    private Container _container = default!;

    public CosmosGrainStorage(
        string name,
        CosmosGrainStorageOptions options,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        IOptions<ClusterOptions> clusterOptions,
        IPartitionKeyProvider partitionKeyProvider
    )
    {
        _logger = loggerFactory.CreateLogger<CosmosGrainStorage>();
        _options = options;
        _name = name;
        _serviceProvider = serviceProvider;
        _serviceId = clusterOptions.Value.ServiceId;
        _partitionKeyProvider = partitionKeyProvider;
        _executor = options.OperationExecutor;
        _partitionKeyPath = _options.PartitionKeyPath;
    }

    public async Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
    {
        var id = GetKeyString(grainId);
        var partitionKey = await BuildPartitionKey(grainType, grainId);

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace(
                "Reading: GrainType={GrainType} Key={Id} GrainId={GrainId} from Container={Container} with PartitionKey={PartitionKey}",
                grainType,
                id,
                grainId,
                _options.ContainerName,
                partitionKey);
        }

        try
        {
            var pk = new PartitionKey(partitionKey);
            var entity = await _executor.ExecuteOperation(static args =>
            {
                var (self, id, pk) = args;
                return self._container.ReadItemAsync<GrainStateEntity<T>>(id, pk);
            },
            (this, id, pk)).ConfigureAwait(false);

            if (entity.Resource.State != null)
            {
                grainState.State = entity.Resource.State;
                grainState.RecordExists = true;
            }
            else
            {
                grainState.State = ActivatorUtilities.CreateInstance<T>(_serviceProvider);
                grainState.RecordExists = false;
            }

            grainState.ETag = entity.Resource.ETag;
        }
        catch (CosmosException dce)
        {
            if (dce.StatusCode == HttpStatusCode.NotFound)
            {
                // State is new, just activate a default and return
                grainState.State = ActivatorUtilities.CreateInstance<T>(_serviceProvider);
                grainState.RecordExists = false;
                return;
            }

            _logger.LogError(dce, "Failure reading state for Grain Type {GrainType} with Id {Id}", grainType, id);
            WrappedException.CreateAndRethrow(dce);
            throw;
        }
        catch (Exception exc)
        {
            _logger.LogError(exc, "Failure reading state for Grain Type {GrainType} with Id {id}", grainType, id);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
    {
        var id = GetKeyString(grainId);

        var partitionKey = await BuildPartitionKey(grainType, grainId);

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace(
                "Writing: GrainType={GrainType} Key={id} GrainId={GrainId} ETag={ETag} from Container={Container} with PartitionKey={PartitionKey}",
                grainType,
                id,
                grainId,
                grainState.ETag,
                _options.ContainerName,
                partitionKey);
        }

        ItemResponse<GrainStateEntity<T>>? response = null;

        try
        {
            var entity = new GrainStateEntity<T>
            {
                ETag = grainState.ETag,
                Id = id,
                GrainType = grainType,
                State = grainState.State,
                PartitionKey = partitionKey
            };

            var pk = new PartitionKey(partitionKey);
            if (string.IsNullOrWhiteSpace(grainState.ETag))
            {
                response = await _executor.ExecuteOperation(
                    static args =>
                    {
                        var (self, entity, pk) = args;
                        return self._container.CreateItemAsync(entity, pk);
                    },
                    (this, entity, pk)).ConfigureAwait(false);

                grainState.ETag = response.Resource.ETag;
            }
            else if (grainState.ETag == ANY_ETAG)
            {
                var requestOptions = new ItemRequestOptions { IfMatchEtag = grainState.ETag };
                response = await _executor.ExecuteOperation(
                    static args =>
                    {
                        var (self, entity, pk, requestOptions) = args;
                        return self._container.UpsertItemAsync(entity, pk, requestOptions);
                    },
                    (this, entity, pk, requestOptions)).ConfigureAwait(false);
                grainState.ETag = response.Resource.ETag;
            }
            else
            {
                var requestOptions = new ItemRequestOptions { IfMatchEtag = grainState.ETag };
                response = await _executor.ExecuteOperation(
                    static args =>
                    {
                        var (self, entity, pk, requestOptions) = args;
                        return self._container.ReplaceItemAsync(entity, entity.Id, pk, requestOptions);
                    },
                    (this, entity, pk, requestOptions)).ConfigureAwait(false);
                grainState.ETag = response.Resource.ETag;
            }

            grainState.RecordExists = true;
        }
        catch (CosmosException dce) when (dce.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new CosmosConditionNotSatisfiedException(grainType, grainId, _options.ContainerName, "Unknown", grainState.ETag);
        }
        catch (Exception exc)
        {
            _logger.LogError(exc, "Failure writing state for Grain Type {GrainType} with Id {Id}", grainType, id);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
    {
        var id = GetKeyString(grainId);
        var partitionKey = await BuildPartitionKey(grainType, grainId);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace(
                "Clearing: GrainType={GrainType} Key={Id} GrainId={GrainId} ETag={ETag} DeleteStateOnClear={DeleteOnClear} from Container={Container} with PartitionKey {PartitionKey}",
                 grainType,
                 id,
                 grainId,
                 grainState.ETag,
                 _options.DeleteStateOnClear,
                 _options.ContainerName,
                 partitionKey);
        }

        var pk = new PartitionKey(partitionKey);
        var requestOptions = new ItemRequestOptions { IfMatchEtag = grainState.ETag };
        try
        {
            if (_options.DeleteStateOnClear)
            {
                if (string.IsNullOrWhiteSpace(grainState.ETag))
                {
                    await ReadStateAsync<T>(grainType, grainId, grainState);
                    if (grainState.RecordExists)
                    {
                        // State exists but the current activation has not observed state creation. Therefore, we have inconsistent state and should throw to give the grain a chance to deactivate and recover.
                        throw new CosmosConditionNotSatisfiedException(grainType, grainId, _options.ContainerName, grainState.ETag, "None");
                    }

                    // State does not exist.
                    return;
                }

                await _executor.ExecuteOperation(static args =>
                {
                    var (self, id, pk, requestOptions) = args;
                    return self._container.DeleteItemAsync<GrainStateEntity<T>>(id, pk, requestOptions);
                },
                (this, id, pk, requestOptions));

                grainState.ETag = null;
                grainState.RecordExists = false;
            }
            else
            {
                var entity = new GrainStateEntity<T>
                {
                    ETag = grainState.ETag,
                    Id = id,
                    GrainType = grainType,
                    State = default!,
                    PartitionKey = partitionKey
                };

                var response = await _executor.ExecuteOperation(static args =>
                {
                    var (self, grainState, entity, pk, requestOptions) = args;
                    return grainState.ETag switch
                    {
                        null or { Length: 0 } => self._container.CreateItemAsync(entity, pk),
                        ANY_ETAG => self._container.ReplaceItemAsync(entity, entity.Id, pk, requestOptions),
                        _ => self._container.ReplaceItemAsync(entity, entity.Id, pk, requestOptions),
                    };
                },
                (this, grainState, entity, pk, requestOptions)).ConfigureAwait(false);

                grainState.ETag = response.Resource.ETag;
                grainState.RecordExists = true;
            }
        }
        catch (Exception exc)
        {
            _logger.LogError(exc, "Failure clearing state for Grain Type {GrainType} with Id {Id}", grainType, id);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public void Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(OptionFormattingUtilities.Name<CosmosGrainStorage>(_name), _options.InitStage, Init);
    }

    private string GetKeyString(GrainId grainId) => $"{Sanitize(_serviceId)}{KEY_STRING_SEPARATOR}{Sanitize(grainId.Type.ToString()!)}{SeparatorChar}{Sanitize(grainId.Key.ToString()!)}";

    private ValueTask<string> BuildPartitionKey(string grainType, GrainId grainId) =>
        _partitionKeyProvider.GetPartitionKey(grainType, grainId);

    private async Task Init(CancellationToken ct)
    {
        var stopWatch = Stopwatch.StartNew();

        try
        {

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Initializing: Name={Name} ServiceId={ServiceId} Collection={Collection} DeleteStateOnClear={DeleteStateOnClear}",
                    _name,
                    _serviceId,
                    _options.ContainerName,
                    _options.DeleteStateOnClear);
            }

            await InitializeCosmosClient().ConfigureAwait(false);

            if (_options.IsResourceCreationEnabled)
            {
                if (_options.CleanResourcesOnInitialization)
                {
                    await TryDeleteDatabase().ConfigureAwait(false);
                }

                await TryCreateResources().ConfigureAwait(false);
            }

            _container = _client.GetContainer(_options.DatabaseName, _options.ContainerName);

            stopWatch.Stop();
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Initializing provider {ProviderName} of type {ProviderType} in stage {Stage} took {ElapsedMilliseconds} milliseconds",
                    _name,
                    GetType().Name,
                    _options.InitStage,
                    stopWatch.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            stopWatch.Stop();
            _logger.LogError(
                (int)ErrorCode.Provider_ErrorFromInit,
                ex,
                "Initialization failed for provider {ProviderName} of type {ProviderType} in stage {Stage} in {ElapsedMilliseconds} milliseconds",
                _name,
                GetType().Name,
                _options.InitStage,
                stopWatch.ElapsedMilliseconds);
            WrappedException.CreateAndRethrow(ex);
            throw;
        }
    }

    private async Task InitializeCosmosClient()
    {
        try
        {
            _client = await _options.CreateClient(_serviceProvider).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing Azure Cosmos DB client for grain storage provider");
            WrappedException.CreateAndRethrow(ex);
            throw;
        }
    }

    private async Task TryCreateResources()
    {
        var dbResponse = await _client.CreateDatabaseIfNotExistsAsync(_options.DatabaseName, _options.DatabaseThroughput);
        var db = dbResponse.Database;

        var stateContainer = new ContainerProperties(_options.ContainerName, _options.PartitionKeyPath);
        stateContainer.IndexingPolicy.IndexingMode = IndexingMode.Consistent;
        stateContainer.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
        stateContainer.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/\"State\"/*" });

        if (_options.StateFieldsToIndex != null)
        {
            foreach (var idx in _options.StateFieldsToIndex)
            {
                var path = idx.StartsWith("/") ? idx[1..] : idx;
                stateContainer.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = $"/\"State\"/\"{path}\"/?" });
            }
        }

        const int maxRetries = 3;
        for (var retry = 0; retry <= maxRetries; ++retry)
        {
            var containerResponse = await db.CreateContainerIfNotExistsAsync(stateContainer, _options.ContainerThroughputProperties);

            if (containerResponse.StatusCode == HttpStatusCode.OK || containerResponse.StatusCode == HttpStatusCode.Created)
            {
                var container = containerResponse.Resource;
                _partitionKeyPath = container.PartitionKeyPath;
                if (_partitionKeyPath == GRAINTYPE_PARTITION_KEY_PATH &&
                    _partitionKeyProvider is not DefaultPartitionKeyProvider)
                    throw new OrleansConfigurationException("Custom partition key provider is not compatible with partition key path set to /GrainType");
            }

            if (retry == maxRetries || dbResponse.StatusCode != HttpStatusCode.Created || containerResponse.StatusCode == HttpStatusCode.Created)
            {
                break;  // Apparently some throttling logic returns HttpStatusCode.OK (not 429) when the collection wasn't created in a new DB.
            }
            await Task.Delay(1000);
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
            _logger.LogError(ex, "Error deleting Azure Cosmos DB database");
            WrappedException.CreateAndRethrow(ex);
            throw;
        }
    }
}

public static class CosmosStorageFactory
{
    public static CosmosGrainStorage Create(IServiceProvider services, string name)
    {
        var optionsMonitor = services.GetRequiredService<IOptionsMonitor<CosmosGrainStorageOptions>>();
        var partitionKeyProvider = services.GetKeyedService<IPartitionKeyProvider>(name)
            ?? services.GetRequiredService<IPartitionKeyProvider>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var clusterOptions = services.GetRequiredService<IOptions<ClusterOptions>>();
        return new CosmosGrainStorage(
            name,
            optionsMonitor.Get(name),
            loggerFactory,
            services,
            clusterOptions,
            partitionKeyProvider);
    }
}