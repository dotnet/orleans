using System.Net;
using System.Threading;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;
using Orleans.Serialization.Serializers;

namespace Orleans.Persistence.Cosmos;

public sealed partial class CosmosGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
{
    private const string ANY_ETAG = "*";
    private const string GRAINTYPE_PARTITION_KEY_PATH = "/GrainType";
    private static readonly string[] HIERARCHICAL_PARTITION_KEY_PATHS = [
        CosmosGrainStorageOptions.DEFAULT_PARTITION_KEY_PATH,
        "/PartitionKey2",
        "/PartitionKey3"
    ];
    private readonly ILogger _logger;
    private readonly CosmosGrainStorageOptions _options;
    private readonly string _name;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _serviceId;
    private readonly string[] _partitionKeyPaths;
    private readonly IDocumentIdProvider _documentIdProvider;
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
        IDocumentIdProvider documentIdProvider,
        IActivatorProvider activatorProvider)
    {
        _logger = loggerFactory.CreateLogger<CosmosGrainStorage>();
        _options = options;
        _name = name;
        _serviceProvider = serviceProvider;
        _serviceId = clusterOptions.Value.ServiceId;
        _documentIdProvider = documentIdProvider;
        _activatorProvider = activatorProvider;
        _executor = options.OperationExecutor;
        _partitionKeyPaths = GetPartitionKeyPaths(options, name);
    }

    public async Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
    {
        var documentKey = await ResolveDocumentKey(grainType, grainId);
        var id = documentKey.DocumentId;

        LogTraceReadingState(grainType, id, grainId, _options.ContainerName, documentKey.PartitionKey.ToString());

        try
        {
            var entity = await _executor.ExecuteOperation(static args =>
            {
                var (self, id, pk) = args;
                return self._container.ReadItemAsync<GrainStateEntity<T>>(id, pk);
            },
            (this, id, documentKey.PartitionKey)).ConfigureAwait(false);

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
        var documentKey = await ResolveDocumentKey(grainType, grainId);
        var id = documentKey.DocumentId;

        LogTraceWritingState(grainType, id, grainId, grainState.ETag, _options.ContainerName, documentKey.PartitionKey.ToString());

        ItemResponse<GrainStateEntity<T>>? response = null;

        try
        {
            var entity = CreateEntity(documentKey, grainType, grainState.State, grainState.ETag);

            var pk = documentKey.PartitionKey;
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
        var documentKey = await ResolveDocumentKey(grainType, grainId);
        var id = documentKey.DocumentId;

        LogTraceClearingState(grainType, id, grainId, grainState.ETag, _options.DeleteStateOnClear, _options.ContainerName, documentKey.PartitionKey.ToString());

        var pk = documentKey.PartitionKey;
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
                var entity = CreateEntity<T>(documentKey, grainType, default, grainState.ETag);

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

    private async Task Init(CancellationToken ct)
    {
        var stopWatch = Stopwatch.StartNew();

        try
        {
            LogDebugInit(_name, _serviceId, _options.ContainerName, _options.DeleteStateOnClear);

            if (_partitionKeyPaths.Length > 1 && _documentIdProvider is DefaultDocumentIdProvider)
            {
                throw new OrleansConfigurationException(
                    $"Azure Cosmos DB grain storage provider '{_name}' is configured for {_partitionKeyPaths.Length}-level hierarchical partition keys, but the default or legacy document identifier provider supplies only one partition-key value. Configure an HPK-aware {nameof(IDocumentIdProvider)}.");
            }

            await InitializeCosmosClient().ConfigureAwait(false);
            _container = _client.GetContainer(_options.DatabaseName, _options.ContainerName);
            var containerValidated = false;

            if (_options.IsResourceCreationEnabled)
            {
                if (_options.CleanResourcesOnInitialization)
                {
                    await TryDeleteDatabase().ConfigureAwait(false);
                }
                else
                {
                    containerValidated = await ValidateContainerPartitionKeyDefinitionIfExists(ct).ConfigureAwait(false);
                }

                if (!containerValidated)
                {
                    await TryCreateResources().ConfigureAwait(false);
                }
            }

            if (!containerValidated)
            {
                await ValidateContainerPartitionKeyDefinition(ct).ConfigureAwait(false);
            }

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

        var stateContainer = _partitionKeyPaths.Length == 1
            ? new ContainerProperties(_options.ContainerName, _partitionKeyPaths[0])
            : new ContainerProperties(_options.ContainerName, _partitionKeyPaths);
        stateContainer.IndexingPolicy.IndexingMode = IndexingMode.Consistent;
        stateContainer.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/*" });
        stateContainer.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = $"/{nameof(GrainStateEntity<object>.GrainType)}/?" });
        foreach (var partitionKeyPath in _partitionKeyPaths)
        {
            stateContainer.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = ToScalarIndexPath(partitionKeyPath) });
        }

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

            if (retry == maxRetries || dbResponse.StatusCode != HttpStatusCode.Created || containerResponse.StatusCode == HttpStatusCode.Created)
            {
                break;  // Apparently some throttling logic returns HttpStatusCode.OK (not 429) when the collection wasn't created in a new DB.
            }
            await Task.Delay(1000);
        }
    }

    private async Task ValidateContainerPartitionKeyDefinition(CancellationToken cancellationToken)
    {
        var response = await _container.ReadContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var containerPaths = response.Resource.PartitionKeyPaths;
        var usesSingleGrainTypePartitioning = _partitionKeyPaths.Length == 1 && containerPaths is [GRAINTYPE_PARTITION_KEY_PATH];

        if (usesSingleGrainTypePartitioning &&
            (_documentIdProvider is not DefaultDocumentIdProvider defaultProvider || defaultProvider.HasCustomPartitionKeyProvider))
        {
            throw new OrleansConfigurationException("Custom document id or partition key providers are not compatible with partition key path set to /GrainType");
        }

        var usesLegacyDefaultPartitioning = usesSingleGrainTypePartitioning &&
            string.Equals(_partitionKeyPaths[0], CosmosGrainStorageOptions.DEFAULT_PARTITION_KEY_PATH, StringComparison.Ordinal);
        if (!usesLegacyDefaultPartitioning)
        {
            ValidateContainerPartitionKeyPaths(_partitionKeyPaths, containerPaths, _name, _options.ContainerName);
        }
    }

    /// <summary>
    /// Validates the existing container, returning <see langword="false"/> when the database or container does not exist.
    /// </summary>
    private async Task<bool> ValidateContainerPartitionKeyDefinitionIfExists(CancellationToken cancellationToken)
    {
        try
        {
            await ValidateContainerPartitionKeyDefinition(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    internal static void ValidateContainerPartitionKeyPaths(
        IReadOnlyList<string> configuredPaths,
        IReadOnlyList<string>? containerPaths,
        string providerName,
        string containerName)
    {
        if (containerPaths is not null && configuredPaths.SequenceEqual(containerPaths, StringComparer.Ordinal))
        {
            return;
        }

        var configuredDescription = DescribePartitionKeyDefinition(configuredPaths);
        var containerDescription = containerPaths is null
            ? "an unavailable partition-key definition"
            : DescribePartitionKeyDefinition(containerPaths);
        throw new OrleansConfigurationException(
            $"Azure Cosmos DB grain storage provider '{providerName}' is configured for {configuredDescription}, but container '{containerName}' uses {containerDescription}. The configured partition-key paths must match the existing container in number, order, and name.");
    }

    internal async ValueTask<ResolvedDocumentKey> ResolveDocumentKey(string grainType, GrainId grainId)
    {
        var documentKey = await _documentIdProvider.GetDocumentKey(grainType, grainId).ConfigureAwait(false);
        var partitionKeyValues = documentKey.PartitionKeyValues?.ToArray();
        if (partitionKeyValues is null || partitionKeyValues.Length != _partitionKeyPaths.Length)
        {
            var actualCount = partitionKeyValues?.Length ?? 0;
            throw new OrleansConfigurationException(
                $"The {nameof(IDocumentIdProvider)} for Azure Cosmos DB grain storage provider '{_name}' returned {actualCount} partition-key value(s) for grain '{grainId}', but {PartitionKeyLevelCountDescription(_partitionKeyPaths.Length)} requires {_partitionKeyPaths.Length}.");
        }

        if (partitionKeyValues.Any(static value => value is null))
        {
            throw new OrleansConfigurationException(
                $"The {nameof(IDocumentIdProvider)} for Azure Cosmos DB grain storage provider '{_name}' returned a null partition-key value for grain '{grainId}'. Partition-key values must be non-null strings.");
        }

        var partitionKey = partitionKeyValues.Length == 1
            ? new PartitionKey(partitionKeyValues[0])
            : BuildHierarchicalPartitionKey(partitionKeyValues);
        return new(documentKey.DocumentId, partitionKeyValues, partitionKey);
    }

    private static PartitionKey BuildHierarchicalPartitionKey(IEnumerable<string> partitionKeyValues)
    {
        var builder = new PartitionKeyBuilder();
        foreach (var value in partitionKeyValues)
        {
            builder.Add(value);
        }

        return builder.Build();
    }

    internal static GrainStateEntity<T> CreateEntity<T>(ResolvedDocumentKey documentKey, string grainType, T? state, string? etag)
    {
        return new GrainStateEntity<T>
        {
            ETag = etag,
            Id = documentKey.DocumentId,
            GrainType = grainType,
            State = state,
            PartitionKey = documentKey.PartitionKeyValues[0],
            PartitionKey2 = documentKey.PartitionKeyValues.Length > 1 ? documentKey.PartitionKeyValues[1] : null,
            PartitionKey3 = documentKey.PartitionKeyValues.Length > 2 ? documentKey.PartitionKeyValues[2] : null
        };
    }

    private static string[] GetPartitionKeyPaths(CosmosGrainStorageOptions options, string providerName)
    {
        if (options.PartitionKeyLevelCount is < 1 or > 3)
        {
            throw new OrleansConfigurationException(
                $"Azure Cosmos DB grain storage provider '{providerName}' has an invalid {nameof(options.PartitionKeyLevelCount)} value of {options.PartitionKeyLevelCount}. Supported values are 1, 2, and 3.");
        }

        if (options.PartitionKeyLevelCount == 1)
        {
            if (string.IsNullOrWhiteSpace(options.PartitionKeyPath))
            {
                throw new OrleansConfigurationException(
                    $"Azure Cosmos DB grain storage provider '{providerName}' has an invalid {nameof(options.PartitionKeyPath)} value.");
            }

            return [options.PartitionKeyPath];
        }

        if (!string.Equals(options.PartitionKeyPath, CosmosGrainStorageOptions.DEFAULT_PARTITION_KEY_PATH, StringComparison.Ordinal))
        {
            throw new OrleansConfigurationException(
                $"Azure Cosmos DB grain storage provider '{providerName}' cannot use a custom {nameof(options.PartitionKeyPath)} with hierarchical partition keys. Hierarchical partition keys use /PartitionKey, /PartitionKey2, and /PartitionKey3 in order.");
        }

        return HIERARCHICAL_PARTITION_KEY_PATHS[..options.PartitionKeyLevelCount];
    }

    private static string DescribePartitionKeyDefinition(IReadOnlyList<string> paths)
    {
        var mode = paths.Count == 1 ? "single-string partitioning" : $"{paths.Count}-level hierarchical partitioning";
        return $"{mode} with path(s) [{string.Join(", ", paths)}]";
    }

    private static string PartitionKeyLevelCountDescription(int levelCount) =>
        levelCount == 1 ? "single-string partitioning" : $"{levelCount}-level hierarchical partitioning";

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

    private static string ToScalarIndexPath(string path)
    {
        if (path.EndsWith("/?", StringComparison.Ordinal))
        {
            return path;
        }

        return path.EndsWith("/", StringComparison.Ordinal) ? $"{path}?" : $"{path}/?";
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
    private partial void LogTraceWritingState(string grainType, string id, GrainId grainId, string? eTag, string container, string partitionKey);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failure writing state for Grain Type {GrainType} with Id {Id}"
    )]
    private partial void LogErrorWritingState(Exception exception, string grainType, string id);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Clearing: GrainType={GrainType} Key={Id} GrainId={GrainId} ETag={ETag} DeleteStateOnClear={DeleteStateOnClear} from Container={Container} with PartitionKey {PartitionKey}"
    )]
    private partial void LogTraceClearingState(string grainType, string id, GrainId grainId, string? eTag, bool deleteStateOnClear, string container, string partitionKey);

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

internal readonly record struct ResolvedDocumentKey(
    string DocumentId,
    string[] PartitionKeyValues,
    PartitionKey PartitionKey);

public static class CosmosStorageFactory
{
    public static CosmosGrainStorage Create(IServiceProvider services, string name)
    {
        var optionsMonitor = services.GetRequiredService<IOptionsMonitor<CosmosGrainStorageOptions>>();
        var clusterOptions = services.GetRequiredService<IOptions<ClusterOptions>>();
        var documentIdProvider = services.GetKeyedService<IDocumentIdProvider>(name);
        if (documentIdProvider is null)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            var partitionKeyProvider = services.GetKeyedService<IPartitionKeyProvider>(name)
                ?? services.GetRequiredService<IPartitionKeyProvider>();
#pragma warning restore CS0618 // Type or member is obsolete
            documentIdProvider = partitionKeyProvider is DefaultPartitionKeyProvider
                ? services.GetRequiredService<IDocumentIdProvider>()
                : new DefaultDocumentIdProvider(clusterOptions, partitionKeyProvider);
        }

        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var activatorProvider = services.GetRequiredService<IActivatorProvider>();
        return new CosmosGrainStorage(
            name,
            optionsMonitor.Get(name),
            loggerFactory,
            services,
            clusterOptions,
            documentIdProvider,
            activatorProvider);
    }
}
