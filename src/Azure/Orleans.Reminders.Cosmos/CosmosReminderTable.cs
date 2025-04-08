using System.Net;
using System.Diagnostics;
using Orleans.Reminders.Cosmos.Models;

namespace Orleans.Reminders.Cosmos;

internal partial class CosmosReminderTable : IReminderTable
{
    private const HttpStatusCode TooManyRequests = (HttpStatusCode)429;
    private const string PARTITION_KEY_PATH = "/PartitionKey";
    private readonly CosmosReminderTableOptions _options;
    private readonly ClusterOptions _clusterOptions;
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly Func<ReminderEntity, ReminderEntry> _convertEntityToEntry;
    private readonly ICosmosOperationExecutor _executor;
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
        _executor = options.Value.OperationExecutor;
    }

    public async Task Init()
    {
        var stopWatch = Stopwatch.StartNew();

        try
        {
            LogDebugInitializingCosmosReminderTable(_clusterOptions.ServiceId, _options.ContainerName);

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

            LogTraceInitializingCosmosReminderTableTook(stopWatch.ElapsedMilliseconds);
        }
        catch (Exception exc)
        {
            stopWatch.Stop();
            LogErrorInitializationFailedForProviderCosmosReminderTable(exc, stopWatch.ElapsedMilliseconds);
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
            var response = await _executor.ExecuteOperation(static async args =>
            {
                var (self, grainId, requestOptions) = args;
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
            (this, grainId, requestOptions)).ConfigureAwait(false);

            return new ReminderTableData(response.Select(_convertEntityToEntry));
        }
        catch (Exception exc)
        {
            LogErrorFailureReadingRemindersForGrain(exc, grainId, _container.Id);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task<ReminderTableData> ReadRows(uint begin, uint end)
    {
        try
        {
            var response = await _executor.ExecuteOperation(static async args =>
            {
                var (self, begin, end) = args;
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
            (this, begin, end)).ConfigureAwait(false);

            return new ReminderTableData(response.Select(_convertEntityToEntry));
        }
        catch (Exception exc)
        {
            LogErrorFailureReadingRemindersForService(exc, _clusterOptions.ServiceId, new(begin), new(end));
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
            var response = await _executor.ExecuteOperation(async args =>
            {
                try
                {
                    var (self, id, pk) = args;
                    var result = await self._container.ReadItemAsync<ReminderEntity>(id, pk).ConfigureAwait(false);
                    return result.Resource;
                }
                catch (CosmosException ce) when (ce.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }
            },
            (this, id, pk)).ConfigureAwait(false);

            return response != null ? FromEntity(response)! : default!;
        }
        catch (Exception exc)
        {
            LogErrorFailureReadingReminder(exc, reminderName, _clusterOptions.ServiceId, grainId);
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

            var response = await _executor.ExecuteOperation(static async args =>
            {
                var (self, pk, entity, options) = args;
                var result = await self._container.UpsertItemAsync(entity, pk, options).ConfigureAwait(false);
                return result.Resource;
            },
            (this, pk, entity, options)).ConfigureAwait(false);

            return response.ETag;
        }
        catch (Exception exc)
        {
            LogErrorFailureToUpsertReminder(exc, _clusterOptions.ServiceId);
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
            await _executor.ExecuteOperation(static args =>
            {
                var (self, id, pk, options) = args;
                return self._container.DeleteItemAsync<ReminderEntity>(id, pk, options);
            },
            (this, id, pk, options)).ConfigureAwait(false);

            return true;
        }
        catch (CosmosException dce) when (dce.StatusCode is HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
        catch (Exception exc)
        {
            LogErrorFailureRemovingReminders(exc, _clusterOptions.ServiceId, grainId, reminderName);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task TestOnlyClearTable()
    {
        try
        {
            var entities = await _executor.ExecuteOperation(static async self =>
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
            }, this).ConfigureAwait(false);

            var deleteTasks = new List<Task>();
            foreach (var entity in entities)
            {
                deleteTasks.Add(_executor.ExecuteOperation(
                    static args =>
                    {
                        var (self, id, pk) = args;
                        return self._container.DeleteItemAsync<ReminderEntity>(id, pk);
                    },
                    (this, entity.Id, new PartitionKey(entity.PartitionKey))));
            }
            await Task.WhenAll(deleteTasks).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            LogErrorFailureToClearReminders(exc, _clusterOptions.ServiceId);
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
            LogErrorInitializingAzureCosmosDbClient(ex);
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
            LogErrorDeletingAzureCosmosDBDatabase(ex);
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

    private readonly struct UIntLogValue(uint value)
    {
        public override string ToString() => value.ToString("X");
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Azure Cosmos DB Reminder Storage CosmosReminderTable is initializing: Name=CosmosReminderTable ServiceId={ServiceId} Collection={Container}"
    )]
    private partial void LogDebugInitializingCosmosReminderTable(string serviceId, string container);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Initializing CosmosReminderTable took {Elapsed} milliseconds"
    )]
    private partial void LogTraceInitializingCosmosReminderTableTook(long elapsed);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Initialization failed for provider CosmosReminderTable in {Elapsed} milliseconds"
    )]
    private partial void LogErrorInitializationFailedForProviderCosmosReminderTable(Exception ex, long elapsed);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failure reading reminders for grain {GrainId} in container {Container}"
    )]
    private partial void LogErrorFailureReadingRemindersForGrain(Exception ex, GrainId grainId, string container);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failure reading reminders for service {Service} for range {Begin} to {End}"
    )]
    private partial void LogErrorFailureReadingRemindersForService(Exception ex, string service, UIntLogValue begin, UIntLogValue end);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failure reading reminder {Name} for service {ServiceId} and grain {GrainId}"
    )]
    private partial void LogErrorFailureReadingReminder(Exception ex, string name, string serviceId, GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failure to upsert reminder for service {ServiceId}"
    )]
    private partial void LogErrorFailureToUpsertReminder(Exception ex, string serviceId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failure removing reminders for service {ServiceId} with GrainId {GrainId} and name {ReminderName}"
    )]
    private partial void LogErrorFailureRemovingReminders(Exception ex, string serviceId, GrainId grainId, string reminderName);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failure to clear reminders for service {ServiceId}"
    )]
    private partial void LogErrorFailureToClearReminders(Exception ex, string serviceId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error initializing Azure Cosmos DB client for membership table provider"
    )]
    private partial void LogErrorInitializingAzureCosmosDbClient(Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error deleting Azure Cosmos DB database"
    )]
    private partial void LogErrorDeletingAzureCosmosDBDatabase(Exception ex);
}