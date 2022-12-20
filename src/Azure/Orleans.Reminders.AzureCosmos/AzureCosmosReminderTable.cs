using System.Net;
using System.Diagnostics;
using Orleans.Reminders.AzureCosmos.Models;

namespace Orleans.Reminders.AzureCosmos;

internal class AzureCosmosReminderTable : IReminderTable
{
    private const HttpStatusCode TooManyRequests = (HttpStatusCode)429;
    private const string PARTITION_KEY_PATH = "/PartitionKey";
    private readonly AzureCosmosReminderTableOptions _options;
    private readonly ClusterOptions _clusterOptions;
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private CosmosClient _client = default!;
    private Container _container = default!;

    public AzureCosmosReminderTable(
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        IOptions<AzureCosmosReminderTableOptions> options,
        IOptions<ClusterOptions> clusterOptions)
    {
        _logger = loggerFactory.CreateLogger<AzureCosmosReminderTable>();
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _clusterOptions = clusterOptions.Value;
    }

    public async Task Init()
    {
        var stopWatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation(
                "Azure Cosmos DB Reminder Storage AzureCosmosReminderTable is initializing: Name=AzureCosmosReminderTable ServiceId={ServiceId} Collection={Container}",
                _clusterOptions.ServiceId,
                _options.Container);

            await InitializeCosmosClient();

            if (_options.IsResourceCreationEnabled)
            {
                if (_options.CleanResourcesOnInitialization)
                {
                    await TryDeleteDatabase();
                }

                await TryCreateCosmosDBResources();
            }

            _container = _client.GetContainer(_options.Database, _options.Container);

            stopWatch.Stop();

            _logger.LogInformation(
                "Initializing AzureCosmosReminderTable took {Elapsed} milliseconds", stopWatch.ElapsedMilliseconds);
        }
        catch (Exception exc)
        {
            stopWatch.Stop();
            _logger.LogError(exc, "Initialization failed for provider AzureCosmosReminderTable in {Elapsed} milliseconds", stopWatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<ReminderTableData> ReadRows(GrainId grainId)
    {
        try
        {
            var response = await ExecuteWithRetries(async () =>
            {
                var pk = new PartitionKey(ReminderEntity.ConstructPartitionKey(_clusterOptions.ServiceId, grainId));

                var query = _container.GetItemLinqQueryable<ReminderEntity>(
                    requestOptions: new QueryRequestOptions { PartitionKey = pk }
                ).ToFeedIterator();

                var reminders = new List<ReminderEntity>();
                do
                {
                    var queryResponse = await query.ReadNextAsync().ConfigureAwait(false);
                    if (queryResponse != null && queryResponse.Count > 0)
                    {
                        reminders.AddRange(queryResponse.ToArray());
                    }
                    else
                    {
                        break;
                    }
                } while (query.HasMoreResults);

                return reminders;
            }).ConfigureAwait(false);

            return new ReminderTableData(response.Select(FromEntity));
        }
        catch (Exception exc)
        {
            _logger.LogError(exc, "Failure reading reminders for grain {GrainId} in container {Container}", grainId, _container.Id);
            throw;
        }
    }

    public async Task<ReminderTableData> ReadRows(uint begin, uint end)
    {
        try
        {
            var response = await ExecuteWithRetries(async () =>
            {
                var query = _container.GetItemLinqQueryable<ReminderEntity>().Where(r => r.ServiceId == _clusterOptions.ServiceId);

                query = begin < end
                    ? query.Where(r => r.GrainHash > begin && r.GrainHash <= end)
                    : query.Where(r => r.GrainHash > begin || r.GrainHash <= end);

                var iterator = query.ToFeedIterator();

                var reminders = new List<ReminderEntity>();

                do
                {
                    var queryResponse = await iterator.ReadNextAsync().ConfigureAwait(false);
                    if (queryResponse != null && queryResponse.Count > 0)
                    {
                        reminders.AddRange(queryResponse.ToArray());
                    }
                    else
                    {
                        break;
                    }
                } while (iterator.HasMoreResults);

                return reminders;
            }).ConfigureAwait(false);

            return new ReminderTableData(response.Select(FromEntity));
        }
        catch (Exception exc)
        {
            _logger.LogError(exc, "Failure reading reminders for service {Service} for range {Begin} to {End}", _clusterOptions.ServiceId, begin, end);
            throw;
        }
    }

    public async Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName)
    {
        try
        {
            var response = await ExecuteWithRetries(async () =>
            {
                var pk = new PartitionKey(ReminderEntity.ConstructPartitionKey(_clusterOptions.ServiceId, grainId));

                ReminderEntity? response = null;
                try
                {
                    response = (await _container.ReadItemAsync<ReminderEntity>(
                        ReminderEntity.ConstructId(grainId, reminderName), pk)
                        .ConfigureAwait(false)).Resource;
                }
                catch (CosmosException ce) when (ce.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                return response;
            }).ConfigureAwait(false);

            return response != null ? FromEntity(response)! : default!;
        }
        catch (Exception exc)
        {
            _logger.LogError(exc, "Failure reading reminder {Name} for service {ServiceId} and grain {GrainId}", reminderName, _clusterOptions.ServiceId, grainId);
            throw;
        }
    }

    public async Task<string> UpsertRow(ReminderEntry entry)
    {
        try
        {
            var entity = ToEntity(entry);

            var response = await ExecuteWithRetries(async () =>
            {
                var pk = new PartitionKey(ReminderEntity.ConstructPartitionKey(_clusterOptions.ServiceId, entry.GrainId));

                return (await _container.UpsertItemAsync(
                    entity,
                    pk,
                    new ItemRequestOptions { IfMatchEtag = entry.ETag }
                ).ConfigureAwait(false)).Resource;
            }).ConfigureAwait(false);

            return response.ETag;
        }
        catch (Exception exc)
        {
            _logger.LogError(exc, "Failure to upsert reminder for service {ServiceId}", _clusterOptions.ServiceId);
            throw;
        }
    }

    public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
    {
        try
        {
            await ExecuteWithRetries(() =>
            {
                var pk = new PartitionKey(ReminderEntity.ConstructPartitionKey(_clusterOptions.ServiceId, grainId));

                return _container.DeleteItemAsync<ReminderEntity>(
                    ReminderEntity.ConstructId(grainId, reminderName),
                    pk,
                    new ItemRequestOptions { IfMatchEtag = eTag }
                );
            }).ConfigureAwait(false);

            return true;
        }
        catch (CosmosException dce) when (dce.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
        catch (Exception exc)
        {
            _logger.LogError(
                exc,
                "Failure removing reminders for service {ServiceId} with GrainId {GrainId} and name {ReminderName}",
                _clusterOptions.ServiceId,
                grainId,
                reminderName);
            throw;
        }
    }

    public async Task TestOnlyClearTable()
    {
        try
        {
            var entities = await ExecuteWithRetries(async () =>
            {
                var query = _container.GetItemLinqQueryable<ReminderEntity>().ToFeedIterator();

                var reminders = new List<ReminderEntity>();
                do
                {
                    var queryResponse = await query.ReadNextAsync().ConfigureAwait(false);
                    if (queryResponse != null && queryResponse.Count > 0)
                    {
                        reminders.AddRange(queryResponse);
                    }
                    else
                    {
                        break;
                    }
                } while (query.HasMoreResults);

                return reminders;
            }).ConfigureAwait(false);

            var deleteTasks = new List<Task>();
            foreach (var entity in entities)
            {
                deleteTasks.Add(ExecuteWithRetries(() => _container.DeleteItemAsync<ReminderEntity>(entity.Id, new PartitionKey(entity.PartitionKey))));
            }
            await Task.WhenAll(deleteTasks).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            _logger.LogError(exc, "Failure to clear reminders for service {ServiceId}", _clusterOptions.ServiceId);
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
            _logger.LogError(ex, "Error initializing Azure Cosmos DB client for membership table provider");
            throw;
        }
    }

    private async Task TryDeleteDatabase()
    {
        try
        {
            await _client.GetDatabase(_options.Database).DeleteAsync();
        }
        catch (CosmosException dce) when (dce.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting Azure Cosmos DB database");
            throw;
        }
    }

    private async Task TryCreateCosmosDBResources()
    {
        var offerThroughput =
            _options.DatabaseThroughput >= 400
            ? (int?)_options.DatabaseThroughput
            : null;

        var dbResponse = await _client.CreateDatabaseIfNotExistsAsync(_options.Database, offerThroughput);
        var db = dbResponse.Database;

        var remindersCollection = new ContainerProperties(_options.Container, PARTITION_KEY_PATH);

        remindersCollection.IndexingPolicy.IndexingMode = IndexingMode.Consistent;
        remindersCollection.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
        remindersCollection.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/StartAt/*" });
        remindersCollection.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/Period/*" });
        remindersCollection.IndexingPolicy.IndexingMode = IndexingMode.Consistent;

        const int maxRetries = 3;
        for (var retry = 0; retry <= maxRetries; ++retry)
        {
            var collResponse = await db.CreateContainerIfNotExistsAsync(
               remindersCollection, _options.GetThroughputProperties());

            if (retry == maxRetries || dbResponse.StatusCode != HttpStatusCode.Created || collResponse.StatusCode == HttpStatusCode.Created)
            {
                break;  // Apparently some throttling logic returns HttpStatusCode.OK (not 429) when the collection wasn't created in a new DB.
            }
            await Task.Delay(1000);
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
            catch (CosmosException dce) when (dce.StatusCode == TooManyRequests)
            {
                sleepTime = dce.RetryAfter ?? dce.RetryAfter!.Value;
            }
            catch (AggregateException ae) when (ae.InnerException is CosmosException dce && dce.StatusCode == TooManyRequests)
            {
                sleepTime = dce.RetryAfter ?? dce.RetryAfter!.Value;
            }
            await Task.Delay(sleepTime);
        }
    }

    private ReminderEntry FromEntity(ReminderEntity entity)
    {
        return new ReminderEntry
        {
            GrainId = GrainId.Parse(entity.GrainId),
            ReminderName = entity.Name,
            Period = entity.Period,
            StartAt = entity.StartAt.UtcDateTime,
            ETag = entity.ETag
        };
    }

    private ReminderEntity ToEntity(ReminderEntry entry)
    {
        return new ReminderEntity
        {
            Id = ReminderEntity.ConstructId(entry.GrainId, entry.ReminderName),
            PartitionKey = ReminderEntity.ConstructPartitionKey(_clusterOptions.ServiceId, entry.GrainId),
            ServiceId = _clusterOptions.ServiceId,
            GrainHash = entry.GrainId.GetUniformHashCode(),
            GrainId = entry.GrainId.ToString(),
            Name = entry.ReminderName,
            StartAt = entry.StartAt,
            Period = entry.Period
        };
    }
}