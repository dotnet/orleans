using System.Net;
using System.Diagnostics;
using Orleans.Reminders.CosmosDB.Models;

namespace Orleans.Reminders.CosmosDB;

internal class AzureCosmosDBReminderTable : IReminderTable
{
    private const HttpStatusCode TooManyRequests = (HttpStatusCode)429;
    private const string PARTITION_KEY_PATH = "/PartitionKey";
    private readonly AzureCosmosDBReminderTableOptions _options;
    private readonly ClusterOptions _clusterOptions;
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private CosmosClient _cosmos = default!;
    private Container _container = default!;

    public AzureCosmosDBReminderTable(
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        IOptions<AzureCosmosDBReminderTableOptions> options,
        IOptions<ClusterOptions> clusterOptions)
    {
        this._logger = loggerFactory.CreateLogger<AzureCosmosDBReminderTable>();
        this._serviceProvider = serviceProvider;
        this._options = options.Value;
        this._clusterOptions = clusterOptions.Value;
    }

    public async Task Init()
    {
        var stopWatch = Stopwatch.StartNew();

        try
        {
            var initMsg = string.Format("Init: Name=AzureCosmosDBReminderTable ServiceId={0} Collection={1}",
                this._clusterOptions.ServiceId, this._options.Container);

            this._logger.LogInformation("Azure Cosmos DB Reminder Storage AzureCosmosDBReminderTable is initializing: {initMsg}", initMsg);

            await this.InitializeCosmosClient();

            if (this._options.IsResourceCreationEnabled)
            {
                if (this._options.CleanResourcesOnInitialization)
                {
                    await this.TryDeleteDatabase();
                }

                await this.TryCreateCosmosDBResources();
            }

            this._container = this._cosmos.GetContainer(this._options.Database, this._options.Container);

            stopWatch.Stop();

            this._logger.LogInformation(
                "Initializing AzureCosmosDBReminderTable took {elapsed} Milliseconds.", stopWatch.ElapsedMilliseconds);
        }
        catch (Exception exc)
        {
            stopWatch.Stop();
            this._logger.LogError(exc, "Initialization failed for provider AzureCosmosDBReminderTable in {elapsed} Milliseconds.", stopWatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<ReminderTableData> ReadRows(GrainId grainId)
    {
        try
        {
            var response = await ExecuteWithRetries(async () =>
            {
                var pk = new PartitionKey(ReminderEntity.ConstructPartitionKey(this._clusterOptions.ServiceId, grainId));

                var query = this._container.GetItemLinqQueryable<ReminderEntity>(
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

            return new ReminderTableData(response.Select(this.FromEntity));
        }
        catch (Exception exc)
        {
            this._logger.LogError(exc, "Failure reading reminders for Grain {GrainId} in container {container}.", grainId, this._container.Id);
            throw;
        }
    }

    public async Task<ReminderTableData> ReadRows(uint begin, uint end)
    {
        try
        {
            var response = await ExecuteWithRetries(async () =>
            {
                var query = this._container.GetItemLinqQueryable<ReminderEntity>().Where(r => r.ServiceId == this._clusterOptions.ServiceId);

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

            return new ReminderTableData(response.Select(this.FromEntity));
        }
        catch (Exception exc)
        {
            this._logger.LogError(exc, "Failure reading reminders for Service {service} for range {begin} to {end}.", this._clusterOptions.ServiceId, begin, end);
            throw;
        }
    }

    public async Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName)
    {
        try
        {
            var response = await ExecuteWithRetries(async () =>
            {
                var pk = new PartitionKey(ReminderEntity.ConstructPartitionKey(this._clusterOptions.ServiceId, grainId));

                ReminderEntity? response = null;
                try
                {
                    response = (await this._container.ReadItemAsync<ReminderEntity>(
                        ReminderEntity.ConstructId(grainId, reminderName), pk)
                        .ConfigureAwait(false)).Resource;
                }
                catch (CosmosException ce) when (ce.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                return response;
            }).ConfigureAwait(false);

            return response != null ? this.FromEntity(response)! : default!;
        }
        catch (Exception exc)
        {
            this._logger.LogError(exc, "Failure reading reminder {name} for service {serviceId} and grain {GrainId}.", reminderName, this._clusterOptions.ServiceId, grainId);
            throw;
        }
    }

    public async Task<string> UpsertRow(ReminderEntry entry)
    {
        try
        {
            var entity = this.ToEntity(entry);

            var response = await ExecuteWithRetries(async () =>
            {
                var pk = new PartitionKey(ReminderEntity.ConstructPartitionKey(this._clusterOptions.ServiceId, entry.GrainId));

                return (await this._container.UpsertItemAsync(
                    entity,
                    pk,
                    new ItemRequestOptions { IfMatchEtag = entry.ETag }
                ).ConfigureAwait(false)).Resource;
            }).ConfigureAwait(false);

            return response.ETag;
        }
        catch (Exception exc)
        {
            this._logger.LogError(exc, "Failure to upsert reminder for Service {serviceId}.", this._clusterOptions.ServiceId);
            throw;
        }
    }

    public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
    {
        try
        {
            await ExecuteWithRetries(() =>
            {
                var pk = new PartitionKey(ReminderEntity.ConstructPartitionKey(this._clusterOptions.ServiceId, grainId));

                return this._container.DeleteItemAsync<ReminderEntity>(
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
            this._logger.LogError(exc,
                "Failure removing reminders for Service {serviceId} with grainId {GrainId} and name {reminderName}.",
                this._clusterOptions.ServiceId, grainId, reminderName);
            throw;
        }
    }

    public async Task TestOnlyClearTable()
    {
        try
        {
            var entities = await ExecuteWithRetries(async () =>
            {
                var query = this._container.GetItemLinqQueryable<ReminderEntity>().ToFeedIterator();

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
                deleteTasks.Add(ExecuteWithRetries(() => this._container.DeleteItemAsync<ReminderEntity>(entity.Id, new PartitionKey(entity.PartitionKey))));
            }
            await Task.WhenAll(deleteTasks).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            this._logger.LogError(exc, "Failure to clear reminders for Service {serviceId}.", this._clusterOptions.ServiceId);
            throw;
        }
    }

    private async Task InitializeCosmosClient()
    {
        try
        {
            this._cosmos = await AzureCosmosDBConnectionFactory.CreateCosmosClient(this._serviceProvider, this._options);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error initializing Azure CosmosDB Client for Membership table provider.");
            throw;
        }
    }

    private async Task TryDeleteDatabase()
    {
        try
        {
            await this._cosmos.GetDatabase(this._options.Database).DeleteAsync();
        }
        catch (CosmosException dce) when (dce.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error deleting Azure CosmosDB database.");
            throw;
        }
    }

    private async Task TryCreateCosmosDBResources()
    {
        var offerThroughput =
            this._options.DatabaseThroughput >= 400
            ? (int?)this._options.DatabaseThroughput
            : null;

        var dbResponse = await this._cosmos.CreateDatabaseIfNotExistsAsync(this._options.Database, offerThroughput);
        var db = dbResponse.Database;

        var remindersCollection = new ContainerProperties(this._options.Container, PARTITION_KEY_PATH);

        remindersCollection.IndexingPolicy.IndexingMode = IndexingMode.Consistent;
        remindersCollection.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
        remindersCollection.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/StartAt/*" });
        remindersCollection.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/Period/*" });
        remindersCollection.IndexingPolicy.IndexingMode = IndexingMode.Consistent;

        const int maxRetries = 3;
        for (var retry = 0; retry <= maxRetries; ++retry)
        {
            var collResponse = await db.CreateContainerIfNotExistsAsync(
               remindersCollection, this._options.GetThroughputProperties());

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
            PartitionKey = ReminderEntity.ConstructPartitionKey(this._clusterOptions.ServiceId, entry.GrainId),
            ServiceId = this._clusterOptions.ServiceId,
            GrainHash = entry.GrainId.GetUniformHashCode(),
            GrainId = entry.GrainId.ToString(),
            Name = entry.ReminderName,
            StartAt = entry.StartAt,
            Period = entry.Period
        };
    }
}