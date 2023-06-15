using System.Net;
using System.Diagnostics;
using Orleans.Reminders.Cosmos.Models;

namespace Orleans.Reminders.Cosmos;

internal class CosmosReminderTable : IReminderTable
{
    private const HttpStatusCode TooManyRequests = (HttpStatusCode)429;
    private const string PARTITION_KEY_PATH = "/PartitionKey";
    private readonly CosmosReminderTableOptions _options;
    private readonly ClusterOptions _clusterOptions;
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly Func<ReminderEntity, ReminderEntry> _convertEntityToEntry;
    private CosmosClient _client = default!;
    private Container _container = default!;

    public CosmosReminderTable(
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        IOptions<CosmosReminderTableOptions> options,
        IOptions<ClusterOptions> clusterOptions)
    {
        _logger = loggerFactory.CreateLogger<CosmosReminderTable>();
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _clusterOptions = clusterOptions.Value;
        _convertEntityToEntry = FromEntity;
    }

    public async Task Init()
    {
        var stopWatch = Stopwatch.StartNew();

        try
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Azure Cosmos DB Reminder Storage CosmosReminderTable is initializing: Name=CosmosReminderTable ServiceId={ServiceId} Collection={Container}",
                    _clusterOptions.ServiceId,
                    _options.ContainerName);
            }

            await InitializeCosmosClient();

            if (_options.IsResourceCreationEnabled)
            {
                if (_options.CleanResourcesOnInitialization)
                {
                    await TryDeleteDatabase();
                }

                await TryCreateCosmosResources();
            }

            _container = _client.GetContainer(_options.DatabaseName, _options.ContainerName);

            stopWatch.Stop();

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace(
                    "Initializing CosmosReminderTable took {Elapsed} milliseconds", stopWatch.ElapsedMilliseconds);
            }
        }
        catch (Exception exc)
        {
            stopWatch.Stop();
            _logger.LogError(exc, "Initialization failed for provider CosmosReminderTable in {Elapsed} milliseconds", stopWatch.ElapsedMilliseconds);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task<ReminderTableData> ReadRows(GrainId grainId)
    {
        try
        {
            var pk = new PartitionKey(ReminderEntity.ConstructPartitionKey(_clusterOptions.ServiceId, grainId));
            var requestOptions = new QueryRequestOptions { PartitionKey = pk };
            var response = await ExecuteWithRetries(static async (self, args) =>
            {
                var (grainId, requestOptions) = args;
                var query = self._container.GetItemLinqQueryable<ReminderEntity>(requestOptions: requestOptions).ToFeedIterator();

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
            },
            (grainId, requestOptions)).ConfigureAwait(false);

            return new ReminderTableData(response.Select(_convertEntityToEntry));
        }
        catch (Exception exc)
        {
            _logger.LogError(exc, "Failure reading reminders for grain {GrainId} in container {Container}", grainId, _container.Id);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task<ReminderTableData> ReadRows(uint begin, uint end)
    {
        try
        {
            var response = await ExecuteWithRetries(static async (self, args) =>
            {
                var (begin, end) = args;
                var query = self._container.GetItemLinqQueryable<ReminderEntity>()
                    .Where(entity => entity.ServiceId == self._clusterOptions.ServiceId);

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
                        reminders.AddRange(queryResponse);
                    }
                    else
                    {
                        break;
                    }
                } while (iterator.HasMoreResults);

                return reminders;
            },
            (begin, end)).ConfigureAwait(false);

            return new ReminderTableData(response.Select(_convertEntityToEntry));
        }
        catch (Exception exc)
        {
            _logger.LogError(
                exc,
                "Failure reading reminders for service {Service} for range {Begin} to {End}",
                _clusterOptions.ServiceId,
                begin.ToString("X"),
                end.ToString("X"));
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName)
    {
        try
        {
            var pk = new PartitionKey(ReminderEntity.ConstructPartitionKey(_clusterOptions.ServiceId, grainId));
            var id = ReminderEntity.ConstructId(grainId, reminderName);
            var response = await ExecuteWithRetries(async (self, args) =>
            {
                try
                {
                    var (id, pk) = args;
                    var result = await self._container.ReadItemAsync<ReminderEntity>(id, pk).ConfigureAwait(false);
                    return result.Resource;
                }
                catch (CosmosException ce) when (ce.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }
            },
            (id, pk)).ConfigureAwait(false);

            return response != null ? FromEntity(response)! : default!;
        }
        catch (Exception exc)
        {
            _logger.LogError(exc, "Failure reading reminder {Name} for service {ServiceId} and grain {GrainId}", reminderName, _clusterOptions.ServiceId, grainId);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task<string> UpsertRow(ReminderEntry entry)
    {
        try
        {
            var entity = ToEntity(entry);
            var pk = new PartitionKey(ReminderEntity.ConstructPartitionKey(_clusterOptions.ServiceId, entry.GrainId));
            var options = new ItemRequestOptions { IfMatchEtag = entity.ETag };

            var response = await ExecuteWithRetries(static async (self, args) =>
            {
                var (pk, entity, options) = args;
                var result = await self._container.UpsertItemAsync(entity, pk, options).ConfigureAwait(false);
                return result.Resource;
            },
            (pk, entity, options)).ConfigureAwait(false);

            return response.ETag;
        }
        catch (Exception exc)
        {
            _logger.LogError(exc, "Failure to upsert reminder for service {ServiceId}", _clusterOptions.ServiceId);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
    {
        try
        {
            var id = ReminderEntity.ConstructId(grainId, reminderName);
            var options = new ItemRequestOptions { IfMatchEtag = eTag, };
            var pk = new PartitionKey(ReminderEntity.ConstructPartitionKey(_clusterOptions.ServiceId, grainId));
            await ExecuteWithRetries(static (self, args) =>
            {
                var (id, pk, options) = args;
                return self._container.DeleteItemAsync<ReminderEntity>(id, pk, options);
            },
            (id, pk, options)).ConfigureAwait(false);

            return true;
        }
        catch (CosmosException dce) when (dce.StatusCode is HttpStatusCode.PreconditionFailed)
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
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task TestOnlyClearTable()
    {
        try
        {
            var entities = await ExecuteWithRetries(static async self =>
            {
                var query = self._container.GetItemLinqQueryable<ReminderEntity>()
                    .Where(entity => entity.ServiceId == self._clusterOptions.ServiceId)
                    .ToFeedIterator();
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
                deleteTasks.Add(ExecuteWithRetries(
                    static (self, args) =>
                    {
                        var (id, pk) = args;
                        return self._container.DeleteItemAsync<ReminderEntity>(id, pk);
                    },
                    (entity.Id, new PartitionKey(entity.PartitionKey))));
            }
            await Task.WhenAll(deleteTasks).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            _logger.LogError(exc, "Failure to clear reminders for service {ServiceId}", _clusterOptions.ServiceId);
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
            _logger.LogError(ex, "Error initializing Azure Cosmos DB client for membership table provider");
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
            _logger.LogError(ex, "Error deleting Azure Cosmos DB database");
            WrappedException.CreateAndRethrow(ex);
            throw;
        }
    }

    private async Task TryCreateCosmosResources()
    {
        var dbResponse = await _client.CreateDatabaseIfNotExistsAsync(_options.DatabaseName, _options.DatabaseThroughput).ConfigureAwait(false);
        var db = dbResponse.Database;

        var remindersCollection = new ContainerProperties(_options.ContainerName, PARTITION_KEY_PATH);

        remindersCollection.IndexingPolicy.IndexingMode = IndexingMode.Consistent;
        remindersCollection.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
        remindersCollection.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/StartAt/*" });
        remindersCollection.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/Period/*" });
        remindersCollection.IndexingPolicy.IndexingMode = IndexingMode.Consistent;

        const int maxRetries = 3;
        for (var retry = 0; retry <= maxRetries; ++retry)
        {
            var collResponse = await db.CreateContainerIfNotExistsAsync(remindersCollection, _options.ContainerThroughputProperties).ConfigureAwait(false);

            if (retry == maxRetries || dbResponse.StatusCode != HttpStatusCode.Created || collResponse.StatusCode == HttpStatusCode.Created)
            {
                break;  // Apparently some throttling logic returns HttpStatusCode.OK (not 429) when the collection wasn't created in a new DB.
            }

            await Task.Delay(1000);
        }
    }

    private async Task<TResult> ExecuteWithRetries<TResult>(Func<CosmosReminderTable, Task<TResult>> clientFunc)
    {
        // From:  https://blogs.msdn.microsoft.com/bigdatasupport/2015/09/02/dealing-with-requestratetoolarge-errors-in-azure-documentdb-and-testing-performance/
        while (true)
        {
            TimeSpan sleepTime;
            try
            {
                return await clientFunc(this).ConfigureAwait(false);
            }
            catch (CosmosException dce) when (dce.StatusCode == TooManyRequests)
            {
                sleepTime = dce.RetryAfter ?? dce.RetryAfter!.Value;
            }
            catch (AggregateException ae) when (ae.InnerException is CosmosException dce && dce.StatusCode == TooManyRequests)
            {
                sleepTime = dce.RetryAfter ?? dce.RetryAfter!.Value;
            }
            await Task.Delay(sleepTime).ConfigureAwait(false);
        }
    }

    private async Task<TResult> ExecuteWithRetries<TArg1, TResult>(Func<CosmosReminderTable, TArg1, Task<TResult>> clientFunc, TArg1 arg1)
    {
        // From:  https://blogs.msdn.microsoft.com/bigdatasupport/2015/09/02/dealing-with-requestratetoolarge-errors-in-azure-documentdb-and-testing-performance/
        while (true)
        {
            TimeSpan sleepTime;
            try
            {
                return await clientFunc(this, arg1).ConfigureAwait(false);
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