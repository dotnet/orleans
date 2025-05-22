using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Orleans.Journaling.Cosmos;

internal sealed class CosmosLogStorageProvider(
    ILoggerFactory loggerFactory,
    IServiceProvider serviceProvider,
    IOptions<ClusterOptions> clusterOptions,
    IOptions<CosmosLogStorageOptions> options)
        : IStateMachineStorageProvider, ILifecycleParticipant<ISiloLifecycle>
{
    [AllowNull] private CosmosClient _client;

    private readonly string _serviceId = clusterOptions.Value.ServiceId;
    private readonly CosmosLogStorageOptions _options = options.Value;
    private readonly ILogger<CosmosLogStorageProvider> _logger = loggerFactory.CreateLogger<CosmosLogStorageProvider>();

    public void Participate(ISiloLifecycle observer) =>
        observer.Subscribe(
            nameof(CosmosLogStorageProvider),
            ServiceLifecycleStage.RuntimeInitialize,
            onStart: Initialize);

    public IStateMachineStorage Create(IGrainContext grainContext)
    {
        var container = _client.GetContainer(_options.DatabaseName, _options.ContainerName);

        return new CosmosLogStorage(grainContext.GrainId, _serviceId, container,
            _options, loggerFactory.CreateLogger<CosmosLogStorage>());
    }

    private async Task Initialize(CancellationToken cancellationToken)
    {
        await InitializeCosmosClient().ConfigureAwait(false);

        if (_options.IsResourceCreationEnabled)
        {
            if (_options.CleanResourcesOnInitialization)
            {
                await TryDeleteDatabase(cancellationToken).ConfigureAwait(false);
            }

            await TryCreateResources(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InitializeCosmosClient()
    {
        try
        {
            _client = await _options.CreateClient(serviceProvider).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing Azure Cosmos DB Client for membership table provider.");
            WrappedException.CreateAndRethrow(ex);

            throw;
        }
    }

    private async Task TryDeleteDatabase(CancellationToken cancellationToken)
    {
        try
        {
            await _client.GetDatabase(_options.DatabaseName)
                .DeleteAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error deleting Azure Cosmos DB database.");
            WrappedException.CreateAndRethrow(ex);

            throw;
        }
    }

    private async Task TryCreateResources(CancellationToken cancellationToken)
    {
        var dbResponse = await _client.CreateDatabaseIfNotExistsAsync(
                _options.DatabaseName, _options.DatabaseThroughput, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var db = dbResponse.Database;

        var logEntryProps = new ContainerProperties(_options.ContainerName, $"/{nameof(LogEntry.LogId)}");

        logEntryProps.IndexingPolicy.IndexingMode = IndexingMode.Consistent;
        logEntryProps.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
        logEntryProps.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = $"/{nameof(LogEntry.Data)}/?" });

        const int maxRetries = 3;

        for (var retry = 0; retry <= maxRetries; ++retry)
        {
            var containerResponse = await db.CreateContainerIfNotExistsAsync(
                logEntryProps, _options.ContainerThroughputProperties,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (containerResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                break;
            }

            if (retry == maxRetries ||
                // If DB was not new, and container creation said OK but was not created
                dbResponse.StatusCode != HttpStatusCode.Created && containerResponse.StatusCode == HttpStatusCode.OK ||
                // If both DB and Container were newly created
                dbResponse.StatusCode == HttpStatusCode.Created && containerResponse.StatusCode == HttpStatusCode.Created)
            {
                break;
            }

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }
    }
}
