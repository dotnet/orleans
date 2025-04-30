using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Persistence.Migration;
using Orleans.Runtime;
using CosmosReminderEntry = Orleans.Reminders.Cosmos.Migration.Models.ReminderEntity;

namespace Orleans.Reminders.Cosmos.Migration
{
    internal class CosmosReminderTable : IReminderTable
    {
        private CosmosClient _client = default!;
        private Container _container = default!;

        private readonly ILogger _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly ClusterOptions _clusterOptions;

        private readonly IGrainReferenceConverter _grainReferenceConverter;
        private readonly IGrainReferenceExtractor _grainReferenceExtractor;
        private readonly CosmosReminderTableOptions _options;

        public CosmosReminderTable(
            ILoggerFactory loggerFactory,
            IServiceProvider serviceProvider,
            CosmosReminderTableOptions options,
            IOptions<ClusterOptions> clusterOptions,
            IGrainReferenceConverter grainReferenceConverter,
            IGrainReferenceExtractor grainReferenceExtractor)
        {
            _logger = loggerFactory.CreateLogger<CosmosReminderTable>();
            _serviceProvider = serviceProvider;
            _clusterOptions = clusterOptions.Value;

            _options = options;
            _grainReferenceConverter = grainReferenceConverter;
            _grainReferenceExtractor = grainReferenceExtractor;
        }


        public async Task Init()
        {
            try
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Azure Cosmos DB Reminder Storage CosmosReminderTable is initializing: Name=CosmosReminderTable ServiceId={ServiceId} Collection={Container}",
                        _clusterOptions.ServiceId,
                        _options.ContainerName);
                }

                _client = await _options.CreateClient(_serviceProvider).ConfigureAwait(false);
                _container = _client.GetContainer(_options.DatabaseName, _options.ContainerName);
            }
            catch (Exception ex)
            {
                WrappedException.CreateAndRethrow(ex);
            }
        }

        public async Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
        {
            try
            {
                var pk = new PartitionKey(CosmosReminderEntry.ConstructPartitionKey(_clusterOptions.ServiceId, grainRef));
                var (grainType, grainInterfaceType, key) = _grainReferenceExtractor.Extract(grainRef);
                var id = CosmosReminderEntry.ConstructId(grainType, key, reminderName);

                var response = await _container.ReadItemAsync<CosmosReminderEntry>(id, pk).ConfigureAwait(false);
                return response != null ? FromEntity(response)! : default!;
            }
            catch (CosmosException cosmosEx) when (cosmosEx.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            catch (Exception exc)
            {
                _logger.LogError(exc, "Failure reading reminder {Name} for service {ServiceId} and grain {GrainId}", reminderName, _clusterOptions.ServiceId, grainRef);
                WrappedException.CreateAndRethrow(exc);
                throw;
            }
        }

        public async Task<ReminderTableData> ReadRows(GrainReference key)
        {
            try
            {
                var pk = new PartitionKey(CosmosReminderEntry.ConstructPartitionKey(_clusterOptions.ServiceId, key));
                var queryRequestOptions = new QueryRequestOptions { PartitionKey = pk };

                var query = _container.GetItemLinqQueryable<CosmosReminderEntry>(requestOptions: queryRequestOptions).ToFeedIterator();
                var reminders = new List<CosmosReminderEntry>();
                
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

                return new ReminderTableData(reminders.Select(FromEntity));
            }
            catch (Exception exc)
            {
                _logger.LogError(exc, "Failure reading reminders for grain {GrainId} in container {Container}", key, _container.Id);
                WrappedException.CreateAndRethrow(exc);
                throw;
            }
        }

        public async Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            try
            {
                var query = _container.GetItemLinqQueryable<CosmosReminderEntry>()
                    .Where(entity => entity.ServiceId == _clusterOptions.ServiceId);

                query = begin < end
                    ? query.Where(r => r.GrainHash > begin && r.GrainHash <= end)
                    : query.Where(r => r.GrainHash > begin || r.GrainHash <= end);

                var iterator = query.ToFeedIterator();
                var reminders = new List<CosmosReminderEntry>();
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

                return new ReminderTableData(reminders.Select(FromEntity));
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

        public async Task<string> UpsertRow(ReminderEntry entry)
        {
            try
            {
                var entity = ToEntity(entry);
                var pk = new PartitionKey(entity.PartitionKey);
                var options = new ItemRequestOptions { IfMatchEtag = entity.ETag };

                var response = await _container.UpsertItemAsync<CosmosReminderEntry>(entity, pk, options).ConfigureAwait(false);
                return response.ETag;
            }
            catch (Exception exc)
            {
                _logger.LogError(exc, "Failure to upsert reminder for service {ServiceId}", _clusterOptions.ServiceId);
                WrappedException.CreateAndRethrow(exc);
                throw;
            }
        }

        public async Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            try
            {
                var (grainType, grainInterfaceType, key) = _grainReferenceExtractor.Extract(grainRef);
                var id = CosmosReminderEntry.ConstructId(grainType, key, reminderName);
                var pk = new PartitionKey(CosmosReminderEntry.ConstructPartitionKey(_clusterOptions.ServiceId, grainRef));
                var options = new ItemRequestOptions { IfMatchEtag = eTag };

                await _container.DeleteItemAsync<CosmosReminderEntry>(id, pk, options).ConfigureAwait(false);

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
                    grainRef,
                    reminderName);
                WrappedException.CreateAndRethrow(exc);
                throw;
            }
        }

        public async Task TestOnlyClearTable()
        {
            try
            {
                var query = _container.GetItemLinqQueryable<CosmosReminderEntry>()
                    .Where(entity => entity.ServiceId == _clusterOptions.ServiceId)
                    .ToFeedIterator();

                var reminders = new List<CosmosReminderEntry>();
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

                var deleteTasks = new List<Task>();
                foreach (var entity in reminders)
                {
                    deleteTasks.Add(_container.DeleteItemAsync<CosmosReminderEntry>(entity.Id, new PartitionKey(entity.PartitionKey)));
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

        private ReminderEntry FromEntity(CosmosReminderEntry entity)
        {
            // a reverse operation - resolving grainReference from grainId in format 'grainType/key'
            var grainRef = _grainReferenceExtractor.ResolveGrainReference(entity.GrainId);

            return new ReminderEntry
            {
                GrainRef = grainRef,
                ReminderName = entity.Name,
                Period = entity.Period,
                StartAt = entity.StartAt.UtcDateTime,
                ETag = entity.ETag
            };
        }

        private CosmosReminderEntry ToEntity(ReminderEntry entry)
        {
            var (grainType, grainInterfaceType, key) = _grainReferenceExtractor.Extract(entry.GrainRef);

            return new CosmosReminderEntry
            {
                Id = CosmosReminderEntry.ConstructId(grainType, key, entry.ReminderName),
                PartitionKey = CosmosReminderEntry.ConstructPartitionKey(_clusterOptions.ServiceId, entry.GrainRef),
                ServiceId = _clusterOptions.ServiceId,
                GrainHash = entry.GrainRef.GetUniformHashCode(),
                GrainId = $"{grainType}/{key}",
                Name = entry.ReminderName,
                StartAt = entry.StartAt,
                Period = entry.Period
            };
        }

        internal static IReminderTable Create(IServiceProvider serviceProvider, string name)
        {
            var grainRefConverter = serviceProvider.GetService<IGrainReferenceConverter>();
            var grainReferenceExtractor = serviceProvider.GetService<IGrainReferenceExtractor>();

            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var clusterOptions = serviceProvider.GetRequiredService<IOptions<ClusterOptions>>();
            var cosmosOptions = serviceProvider.GetRequiredService<IOptionsMonitor<CosmosReminderTableOptions>>().Get(name);

            return new CosmosReminderTable(loggerFactory, serviceProvider, cosmosOptions, clusterOptions, grainRefConverter, grainReferenceExtractor);
        }
    }
}
