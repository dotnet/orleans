using System.Net;
using System.Threading;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;
using static Orleans.Persistence.Cosmos.CosmosIdSanitizer;
using Orleans.Serialization.Serializers;

namespace Orleans.Persistence.Cosmos;

public sealed partial class CosmosGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
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
    private readonly IActivatorProvider _activatorProvider;
    private readonly ICosmosOperationExecutor _executor;
    private CosmosClient _client = default!;
    private Container _container = default!;

    public CosmosGrainStorage(
        string name,
        CosmosGrainStorageOptions options,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        IOptions<ClusterOptions> clusterOptions,
        IPartitionKeyProvider partitionKeyProvider,
        IActivatorProvider activatorProvider)
    {
        _logger = loggerFactory.CreateLogger<CosmosGrainStorage>();
        _options = options;
        _name = name;
        _serviceProvider = serviceProvider;
        _serviceId = clusterOptions.Value.ServiceId;
        _partitionKeyProvider = partitionKeyProvider;
        _activatorProvider = activatorProvider;
        _executor = options.OperationExecutor;
        _partitionKeyPath = _options.PartitionKeyPath;
    }

    public async Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
    {
        var id = GetKeyString(grainId);
        var partitionKey = await BuildPartitionKey(grainType, grainId);

        LogTraceReadingState(grainType, id, grainId, _options.ContainerName, partitionKey);

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
                grainState.State = CreateInstance<T>();
                grainState.RecordExists = false;
            }

            grainState.ETag = entity.Resource.ETag;
        }
        catch (CosmosException dce)
        {
            if (dce.StatusCode == HttpStatusCode.NotFound)
            {
                // State is new, just activate a default and return.
                ResetGrainState(grainState);
                return;
            }

            LogErrorReadingState(dce, grainType, id);
            WrappedException.CreateAndRethrow(dce);
            throw;
        }
        catch (Exception exc)
        {
            LogErrorReadingState(exc, grainType, id);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
    {
        var id = GetKeyString(grainId);

        var partitionKey = await BuildPartitionKey(grainType, grainId);

        LogTraceWritingState(grainType, id, grainId, grainState.ETag, _options.ContainerName, partitionKey);

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
            }

            grainState.ETag = response.Resource.ETag;
            grainState.RecordExists = true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            throw new CosmosConditionNotSatisfiedException(grainType, grainId, _options.ContainerName, "Unknown", grainState.ETag);
        }
        catch (Exception exc)
        {
            LogErrorWritingState(exc, grainType, id);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
    {
        var id = GetKeyString(grainId);
        var partitionKey = await BuildPartitionKey(grainType, grainId);

        LogTraceClearingState(grainType, id, grainId, grainState.ETag, _options.DeleteStateOnClear, _options.ContainerName, partitionKey);

        var pk = new PartitionKey(partitionKey);
        var requestOptions = new ItemRequestOptions { IfMatchEtag = grainState.ETag };
        try
        {
            if (_options.DeleteStateOnClear)
            {
                if (string.IsNullOrWhiteSpace(grainState.ETag))
                {
                    try
                    {
                        var entity = await _executor.ExecuteOperation(static args =>
                        {
                            var (self, id, pk) = args;
                            return self._container.ReadItemAsync<GrainStateEntity<T>>(id, pk);
                        },
                        (this, id, pk)).ConfigureAwait(false);

                        // State exists but the current activation has not observed state creation. Therefore, we have inconsistent
                        // state and should throw to give the grain a chance to deactivate and recover.
                        throw new CosmosConditionNotSatisfiedException(grainType, grainId, _options.ContainerName, "None", entity.ETag);
                    }
                    catch (CosmosException dce) when (dce.StatusCode == HttpStatusCode.NotFound)
                    {
                        // Ignore, since this is the expected outcome.
                        // All other exceptions will be handled by the outer catch blocks.
                    }
                }
                else
                {
                    await _executor.ExecuteOperation(static args =>
                    {
                        var (self, id, pk, requestOptions) = args;
                        return self._container.DeleteItemAsync<GrainStateEntity<T>>(id, pk, requestOptions);
                    },
                    (this, id, pk, requestOptions));
                }

                ResetGrainState(grainState);
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
                grainState.RecordExists = false;
                grainState.State = CreateInstance<T>();
            }
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            throw new CosmosConditionNotSatisfiedException(grainType, grainId, _options.ContainerName, "Unknown", grainState?.ETag ?? "Unknown");
        }
        catch (Exception exc)
        {
            LogErrorClearingState(exc, grainType, id);
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
            LogDebugInit(_name, _serviceId, _options.ContainerName, _options.DeleteStateOnClear);

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
            LogDebugInitializingProvider(_name, GetType().Name, _options.InitStage, stopWatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopWatch.Stop();
            LogErrorInitializationFailed(ex, _name, GetType().Name, _options.InitStage, stopWatch.ElapsedMilliseconds);
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
            LogErrorInitializingClient(ex);
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
            LogErrorDeletingDatabase(ex);
            WrappedException.CreateAndRethrow(ex);
            throw;
        }
    }

    private void ResetGrainState<T>(IGrainState<T> grainState)
    {
        grainState.State = CreateInstance<T>();
        grainState.ETag = null;
        grainState.RecordExists = false;
    }

    private T CreateInstance<T>() => _activatorProvider.GetActivator<T>().Create();

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Reading: GrainType={GrainType} Key={Id} GrainId={GrainId} from Container={Container} with PartitionKey={PartitionKey}"
    )]
    private partial void LogTraceReadingState(string grainType, string id, GrainId grainId, string container, string partitionKey);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failure reading state for Grain Type {GrainType} with Id {Id}"
    )]
    private partial void LogErrorReadingState(Exception exception, string grainType, string id);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Writing: GrainType={GrainType} Key={Id} GrainId={GrainId} ETag={ETag} from Container={Container} with PartitionKey={PartitionKey}"
    )]
    private partial void LogTraceWritingState(string grainType, string id, GrainId grainId, string eTag, string container, string partitionKey);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failure writing state for Grain Type {GrainType} with Id {Id}"
    )]
    private partial void LogErrorWritingState(Exception exception, string grainType, string id);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Clearing: GrainType={GrainType} Key={Id} GrainId={GrainId} ETag={ETag} DeleteStateOnClear={DeleteStateOnClear} from Container={Container} with PartitionKey {PartitionKey}"
    )]
    private partial void LogTraceClearingState(string grainType, string id, GrainId grainId, string eTag, bool deleteStateOnClear, string container, string partitionKey);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failure clearing state for Grain Type {GrainType} with Id {Id}"
    )]
    private partial void LogErrorClearingState(Exception exception, string grainType, string id);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Initializing: Name={Name} ServiceId={ServiceId} Collection={Collection} DeleteStateOnClear={DeleteStateOnClear}"
    )]
    private partial void LogDebugInit(string name, string serviceId, string collection, bool deleteStateOnClear);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Initializing provider {ProviderName} of type {ProviderType} in stage {Stage} took {ElapsedMilliseconds} milliseconds"
    )]
    private partial void LogDebugInitializingProvider(string providerName, string providerType, int stage, long elapsedMilliseconds);

    [LoggerMessage(
        EventId = (int)ErrorCode.Provider_ErrorFromInit,
        Level = LogLevel.Error,
        Message = "Initialization failed for provider {ProviderName} of type {ProviderType} in stage {Stage} in {ElapsedMilliseconds} milliseconds"
    )]
    private partial void LogErrorInitializationFailed(Exception exception, string providerName, string providerType, int stage, long elapsedMilliseconds);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error initializing Azure Cosmos DB client for grain storage provider"
    )]
    private partial void LogErrorInitializingClient(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error deleting Azure Cosmos DB database"
    )]
    private partial void LogErrorDeletingDatabase(Exception exception);
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
        var activatorProvider = services.GetRequiredService<IActivatorProvider>();
        return new CosmosGrainStorage(
            name,
            optionsMonitor.Get(name),
            loggerFactory,
            services,
            clusterOptions,
            partitionKeyProvider,
            activatorProvider);
    }
}
