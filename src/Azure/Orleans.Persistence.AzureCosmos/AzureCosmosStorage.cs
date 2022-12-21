using System.Net;
using System.Threading;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace Orleans.Persistence.AzureCosmos;

internal class AzureCosmosStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
{
    private const string KEY_STRING_SEPARATOR = "__";
    private const string DEFAULT_PARTITION_KEY_PATH = "/PartitionKey";
    private const string GRAINTYPE_PARTITION_KEY_PATH = "/GrainType";
    private const HttpStatusCode TOO_MANY_REQUESTS = (HttpStatusCode)429;
    private readonly ILogger _logger;
    private readonly AzureCosmosGrainStorageOptions _options;
    private readonly string _name;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _serviceId;
    private string _partitionKeyPath = DEFAULT_PARTITION_KEY_PATH;
    private readonly IPartitionKeyProvider _partitionKeyProvider;
    private CosmosClient _client = default!;
    private Container _container = default!;

    public AzureCosmosStorage(
        string name,
        AzureCosmosGrainStorageOptions options,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        ClusterOptions clusterOptions,
        IPartitionKeyProvider partitionKeyProvider
    )
    {
        _logger = loggerFactory.CreateLogger<AzureCosmosStorage>();
        _options = options;
        _name = name;
        _serviceProvider = serviceProvider;
        _serviceId = clusterOptions.ServiceId;
        _partitionKeyProvider = partitionKeyProvider;
    }

    public async Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
    {
        var id = GetKeyString(grainId);
        var partitionKey = await BuildPartitionKey(grainType, grainId);

        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace(
            "Reading: GrainType={grainType} Key={id} Grainid={grainReference} from Container={container} with PartitionKey={pk}",
            grainType, id, grainId, _options.Container, partitionKey);

        try
        {
            var entity = await ExecuteWithRetries(async () => await _container.ReadItemAsync<GrainStateEntity<T>>(
                id, new PartitionKey(partitionKey))).ConfigureAwait(false);

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

            _logger.LogError(dce, "Failure reading state for Grain Type {GrainType} with Id {id}", grainType, id);
            WrappedException.CreateAndRethrow(dce);
            throw;
        }
        catch (Exception exc)
        {
            _logger.LogError(exc, "Failure reading state for Grain Type {GrainType} with Id {Id}", grainType, id);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
    {
        var id = GetKeyString(grainId);

        var partitionKey = await BuildPartitionKey(grainType, grainId);

        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace(
            "Writing: GrainType={grainType} Key={id} Grainid={grainReference} ETag={etag} from Container={container} with PartitionKey={pk}",
            grainType, id, grainId, grainState.ETag, _options.Container, partitionKey);

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

            if (string.IsNullOrWhiteSpace(grainState.ETag))
            {
                response = await ExecuteWithRetries(() => _container.CreateItemAsync(
                   entity,
                   new PartitionKey(partitionKey))).ConfigureAwait(false);

                grainState.ETag = response.Resource.ETag;
            }
            else
            {
                response = await ExecuteWithRetries(() =>
                    _container.ReplaceItemAsync(
                        entity, entity.Id,
                        new PartitionKey(partitionKey),
                        new ItemRequestOptions { IfMatchEtag = grainState.ETag }))
                    .ConfigureAwait(false);
                grainState.ETag = response.Resource.ETag;
            }

            grainState.RecordExists = true;
        }
        catch (CosmosException dce) when (dce.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new CosmosConditionNotSatisfiedException(grainType, grainId, _options.Container, "Unknown", grainState.ETag);
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
        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace(
            "Clearing: GrainType={grainType} Key={id} Grainid={grainReference} ETag={etag} DeleteStateOnClear={deleteOnClear} from Container={container} with PartitionKey {pk}",
            grainType, id, grainId, grainState.ETag, _options.DeleteStateOnClear, _options.Container, partitionKey);

        var pk = new PartitionKey(partitionKey);
        var requestOptions = new ItemRequestOptions { IfMatchEtag = grainState.ETag };
        try
        {
            if (_options.DeleteStateOnClear)
            {
                if (string.IsNullOrWhiteSpace(grainState.ETag))
                    return;  //state not written

                await ExecuteWithRetries(() => _container.DeleteItemAsync<GrainStateEntity<T>>(
                    id, pk, requestOptions));

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

                var response = await ExecuteWithRetries(() =>
                    string.IsNullOrWhiteSpace(grainState.ETag) ?
                        _container.CreateItemAsync(entity, pk) :
                        _container.ReplaceItemAsync(entity, entity.Id, pk, requestOptions))
                    .ConfigureAwait(false);

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
        lifecycle.Subscribe(OptionFormattingUtilities.Name<AzureCosmosStorage>(_name), _options.InitStage, Init);
    }

    private string GetKeyString(GrainId grainId) => $"{_serviceId}{KEY_STRING_SEPARATOR}{System.Net.WebUtility.UrlEncode(grainId.ToString())}";

    private ValueTask<string> BuildPartitionKey(string grainType, GrainId grainId) =>
        _partitionKeyProvider.GetPartitionKey(grainType, grainId);

    private async Task Init(CancellationToken ct)
    {
        var stopWatch = Stopwatch.StartNew();

        try
        {
            var initMsg = string.Format("Init: Name={0} ServiceId={1} Collection={2} DeleteStateOnClear={3}",
                _name, _serviceId, _options.Container, _options.DeleteStateOnClear);

            _logger.LogInformation(initMsg);

            await InitializeCosmosClient().ConfigureAwait(false);

            if (_options.IsResourceCreationEnabled)
            {
                if (_options.CleanResourcesOnInitialization)
                {
                    await TryDeleteDatabase().ConfigureAwait(false);
                }

                await TryCreateResources().ConfigureAwait(false);
            }

            _container = _client.GetContainer(_options.Database, _options.Container);

            stopWatch.Stop();
            _logger.LogInformation(
                "Initializing provider {ProviderName} of type {ProviderType} in stage {Stage} took {ElapsedMilliseconds} milliseconds",
                _name,
                GetType().Name,
                _options.InitStage,
                stopWatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopWatch.Stop();
            _logger.LogError((int)ErrorCode.Provider_ErrorFromInit,
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
        var dbThroughput =
            _options.DatabaseThroughput >= 400
            ? (int?)_options.DatabaseThroughput
            : null;

        var dbResponse = await _client.CreateDatabaseIfNotExistsAsync(_options.Database, dbThroughput);
        var db = dbResponse.Database;

        var stateContainer = new ContainerProperties(_options.Container, DEFAULT_PARTITION_KEY_PATH);
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
            var containerResponse = await db.CreateContainerIfNotExistsAsync(
                stateContainer, _options.GetThroughputProperties());

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
            await _client.GetDatabase(_options.Database).DeleteAsync().ConfigureAwait(false);
        }
        catch (CosmosException dce) when (dce.StatusCode == System.Net.HttpStatusCode.NotFound)
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

    private static async Task<TResult> ExecuteWithRetries<TResult>(Func<Task<TResult>> clientFunc)
    {
        // From:  https://blogs.msdn.microsoft.com/bigdatasupport/2015/09/02/dealing-with-requestratetoolarge-errors-in-azure-documentdb-and-testing-performance/
        while (true)
        {
            TimeSpan sleepTime;
            try
            {
                return await clientFunc();
            }
            catch (CosmosException dce) when (dce.StatusCode == TOO_MANY_REQUESTS)
            {
                sleepTime = dce.RetryAfter ?? TimeSpan.Zero;
            }
            catch (AggregateException ae) when (ae.InnerException is CosmosException dce && dce.StatusCode == TOO_MANY_REQUESTS)
            {
                sleepTime = dce.RetryAfter ?? TimeSpan.Zero;
            }
            await Task.Delay(sleepTime);
        }
    }
}

public static class AzureCosmosStorageFactory
{
    public static IGrainStorage Create(IServiceProvider services, string name)
    {
        var optionsMonitor = services.GetRequiredService<IOptionsMonitor<AzureCosmosGrainStorageOptions>>();
        return ActivatorUtilities.CreateInstance<AzureCosmosStorage>(services, name, optionsMonitor.Get(name));
    }
}