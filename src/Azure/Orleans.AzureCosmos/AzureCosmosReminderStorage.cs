using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Orleans.AzureCosmos
{
    internal sealed class AzureCosmosReminderStorage : AzureCosmosStorage, IReminderTable
    {
        private readonly AzureCosmosReminderOptions options;
        private readonly string partitionPrefix;
        private readonly GrainReferenceKeyStringConverter grainReferenceConverter;

        public AzureCosmosReminderStorage(
            IOptions<AzureCosmosReminderOptions> options,
            IOptions<ClusterOptions> clusterOptions,
            GrainReferenceKeyStringConverter grainReferenceConverter,
            ILoggerFactory loggerFactory)
            : base(loggerFactory)
        {
            this.options = options.Value;
            this.partitionPrefix = clusterOptions.Value.ServiceId + "/";
            this.grainReferenceConverter = grainReferenceConverter;
        }

        public Task Init()
        {
            logger.Info("Initializing reminders container for service id {0}", partitionPrefix);
            return Init(options, new()
            {
                PartitionKeyPath = "/" + nameof(ReminderRecord.HashRange),
                IndexingPolicy = new()
                {
                    ExcludedPaths = { new() { Path = "/*" } },
                    IncludedPaths = { new() { Path = "/" + nameof(ReminderRecord.HashRange) + "/?" } }
                }
            });
        }

        public async Task<ReminderEntry> ReadRow(GrainReference grainRef, string reminderName)
        {
            try
            {
                var pk = GetKeyString(grainRef);
                var rowKey = GetRowKey(grainRef, reminderName);
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Reading: RowKey={0} PK={1} GrainId={2} from Container={3}", rowKey, pk, grainRef, options.ContainerName);

                var startTime = DateTime.UtcNow;
                // Task.Run is a workaround for https://github.com/Azure/azure-cosmos-dotnet-v2/issues/687
                using var res = await Task.Run(() => container.ReadItemStreamAsync(rowKey, new PartitionKey(pk)));
                CheckAlertSlowAccess(startTime, "ReadItem");

                if (res.StatusCode == HttpStatusCode.NotFound)
                {
                    if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("NotFound reading: PartitionKey={0} RowKey={1} from Container={2}", pk, rowKey, options.ContainerName);
                    return null;
                }

                res.EnsureSuccessStatusCode();
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Read: PartitionKey={0} RowKey={1} from Container={2} with ETag={3}", pk, rowKey, options.ContainerName, res.Headers.ETag);
                return AsReminderEntry(Deserialize<ReminderRecord>(res));
            }
            catch (Exception ex) when (Log(ex)) { throw; }
        }

        public Task<ReminderTableData> ReadRows(GrainReference grainRef)
        {
            try
            {
                var pk = GetKeyString(grainRef);
                var rowKey = GetRowKey(grainRef, null);
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Reading: RowKeyPrefix={0} PK={1} GrainId={2} from Container={3}", rowKey, pk, grainRef, options.ContainerName);

                return ReadRows(new QueryDefinition("SELECT * FROM c WHERE STARTSWITH(c.id, @id)")
                    .WithParameter("@id", rowKey),
                    requestOptions: new() { PartitionKey = new(pk) });
            }
            catch (Exception ex) when (Log(ex)) { throw; }
        }

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            try
            {
                string pkBegin = GetKeyString(begin), pkEnd = GetKeyString(end);
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Reading: begin={0} end={1} from Container={2}", pkBegin, pkEnd, options.ContainerName);
                var query = "SELECT * FROM c WHERE c.HashRange>@b AND c.HashRange<=@e";
                var sql = begin < end
                    ? new QueryDefinition(query)
                        .WithParameter("@b", pkBegin).WithParameter("@e", pkEnd)
                    : new QueryDefinition(query + " OR c.HashRange>@bx AND c.HashRange<=@ex")
                        .WithParameter("@b", partitionPrefix).WithParameter("@e", pkEnd)
                        .WithParameter("@bx", pkBegin).WithParameter("@ex", GetKeyString(uint.MaxValue));
                return ReadRows(sql);
            }
            catch (Exception ex) when (Log(ex)) { throw; }
        }

        private async Task<ReminderTableData> ReadRows(QueryDefinition sql, QueryRequestOptions requestOptions = null)
        {
            try
            {
                using var query = container.GetItemQueryStreamIterator(sql, null, requestOptions);

                var startTime = DateTime.UtcNow;
                var ls = new List<ReminderRecord>();
                do
                {
                    // Task.Run is a workaround for https://github.com/Azure/azure-cosmos-dotnet-v2/issues/687
                    using var res = await Task.Run(() => query.ReadNextAsync());
                    res.EnsureSuccessStatusCode();
                    ls.AddRange(Deserialize<QueryResponse>(res).Documents);
                } while (query.HasMoreResults);
                CheckAlertSlowAccess(startTime, "ReadItems");

                var data = new ReminderTableData(ls.Select(AsReminderEntry));
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Read reminders table:\n{0}", data);
                return data;
            }
            catch (Exception ex) when (Log(ex)) { throw; }
        }

        public async Task<string> UpsertRow(ReminderEntry entry)
        {
            try
            {
                var record = AsReminderRecord(entry);
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Writing: RowKey={0} PK={1} GrainId={2} to Container={3}", record.Id, record.HashRange, entry.GrainRef, options.ContainerName);

                var payload = record.Serialize();

                var startTime = DateTime.UtcNow;
                // Task.Run is a workaround for https://github.com/Azure/azure-cosmos-dotnet-v2/issues/687
                using var res = await Task.Run(() => container.UpsertItemStreamAsync(payload, new PartitionKey(record.HashRange), noContentResponse));
                CheckAlertSlowAccess(startTime, "UpsertItem");

                var eTag = res.EnsureSuccessStatusCode().Headers.ETag;
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Wrote: PartitionKey={0} RowKey={1} to Container={2} with ETag={3}", record.HashRange, record.Id, options.ContainerName, eTag);
                return eTag;
            }
            catch (Exception ex) when (Log(ex)) { throw; }
        }

        public async Task<bool> RemoveRow(GrainReference grainRef, string reminderName, string eTag)
        {
            try
            {
                var pk = GetKeyString(grainRef);
                var rowKey = GetRowKey(grainRef, reminderName);
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Deleting: RowKey={0} PK={1} GrainId={2} from Container={3}", rowKey, pk, grainRef, options.ContainerName);

                var startTime = DateTime.UtcNow;
                // Task.Run is a workaround for https://github.com/Azure/azure-cosmos-dotnet-v2/issues/687
                using var res = await Task.Run(() => container.DeleteItemStreamAsync(rowKey, new PartitionKey(pk)));
                CheckAlertSlowAccess(startTime, "DeleteItem");

                if (res.StatusCode == HttpStatusCode.NotFound)
                {
                    if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Reminder was not found when deleting: RowKey={0} PK={1} from Container={2}", rowKey, pk, options.ContainerName);
                    return true;
                }

                res.EnsureSuccessStatusCode();
                if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Deleted: RowKey={0} PK={1} from Container={2}", rowKey, pk, options.ContainerName);
                return true;
            }
            catch (Exception ex) when (Log(ex)) { throw; }
        }

        public Task TestOnlyClearTable() => throw new NotSupportedException();

        private string GetKeyString(GrainReference grainRef) => GetKeyString(grainRef.GetUniformHashCode());
        private string GetKeyString(uint hash) => partitionPrefix + hash.ToString("X8");

        private static string GetRowKey(GrainReference grainRef, string reminderName) => grainRef.ToKeyString() + "$" + reminderName;

        private sealed class ReminderRecord : RecordBase
        {
            public string HashRange { get; set; }

            public DateTime StartAt { get; set; }
            public long Period { get; set; }
        }

        private sealed class QueryResponse
        {
            public ReminderRecord[] Documents { get; set; }
        }

        private ReminderEntry AsReminderEntry(ReminderRecord r)
        {
            var i = r.Id.IndexOf('$');
            var key = r.Id.Substring(0, i);
            var reminderName = r.Id.Substring(i + 1);
            return new()
            {
                GrainRef = grainReferenceConverter.FromKeyString(key),
                ReminderName = reminderName,
                StartAt = r.StartAt,
                Period = new TimeSpan(r.Period),
                ETag = r.ETag,
            };
        }

        private ReminderRecord AsReminderRecord(ReminderEntry r) => new()
        {
            Id = GetRowKey(r.GrainRef, r.ReminderName),
            HashRange = GetKeyString(r.GrainRef),
            StartAt = r.StartAt,
            Period = r.Period.Ticks,
        };
    }
}
