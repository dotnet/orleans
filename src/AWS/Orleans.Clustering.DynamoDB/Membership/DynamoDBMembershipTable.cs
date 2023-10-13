using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Clustering.DynamoDB
{
    internal class DynamoDBMembershipTable : IMembershipTable
    {
        private static readonly TableVersion NotFoundTableVersion = new TableVersion(0, "0");

        private const string CURRENT_ETAG_ALIAS = ":currentETag";
        private const int MAX_BATCH_SIZE = 25;

        private readonly ILogger logger;
        private DynamoDBStorage storage;
        private readonly DynamoDBClusteringOptions options;
        private readonly string clusterId;

        public DynamoDBMembershipTable(
            ILoggerFactory loggerFactory,
            IOptions<DynamoDBClusteringOptions> clusteringOptions,
            IOptions<ClusterOptions> clusterOptions)
        {
            logger = loggerFactory.CreateLogger<DynamoDBMembershipTable>();
            this.options = clusteringOptions.Value;
            this.clusterId = clusterOptions.Value.ClusterId;
        }

        public async Task InitializeMembershipTable(bool tryInitTableVersion)
        {
            this.storage = new DynamoDBStorage(
                this.logger,
                this.options.Service,
                this.options.AccessKey,
                this.options.SecretKey,
                this.options.Token,
                this.options.ProfileName,
                this.options.ReadCapacityUnits,
                this.options.WriteCapacityUnits,
                this.options.UseProvisionedThroughput,
                this.options.CreateIfNotExists,
                this.options.UpdateIfExists);

            logger.LogInformation((int)ErrorCode.MembershipBase, "Initializing AWS DynamoDB Membership Table");
            await storage.InitializeTable(this.options.TableName,
                new List<KeySchemaElement>
                {
                    new KeySchemaElement { AttributeName = SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME, KeyType = KeyType.HASH },
                    new KeySchemaElement { AttributeName = SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME, KeyType = KeyType.RANGE }
                },
                new List<AttributeDefinition>
                {
                    new AttributeDefinition { AttributeName = SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME, AttributeType = ScalarAttributeType.S },
                    new AttributeDefinition { AttributeName = SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME, AttributeType = ScalarAttributeType.S }
                });

            // even if I am not the one who created the table,
            // try to insert an initial table version if it is not already there,
            // so we always have a first table version row, before this silo starts working.
            if (tryInitTableVersion)
            {
                // ignore return value, since we don't care if I inserted it or not, as long as it is in there.
                bool created = await TryCreateTableVersionEntryAsync();
                if(created) logger.LogInformation("Created new table version row.");
            }
        }

        private async Task<bool> TryCreateTableVersionEntryAsync()
        {
            var keys = new Dictionary<string, AttributeValue>
            {
                { $"{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", new AttributeValue(this.clusterId) },
                { $"{SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME}", new AttributeValue(SiloInstanceRecord.TABLE_VERSION_ROW) }
            };

            var versionRow = await storage.ReadSingleEntryAsync(this.options.TableName, keys, fields => new SiloInstanceRecord(fields));
            if (versionRow != null)
            {
                return false;
            }

            if (!TryCreateTableVersionRecord(0, null, out var entry))
            {
                return false;
            }

            var notExistConditionExpression =
                $"attribute_not_exists({SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}) AND attribute_not_exists({SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME})";
            try
            {
                await storage.PutEntryAsync(this.options.TableName, entry.GetFields(true), notExistConditionExpression);
            }
            catch (ConditionalCheckFailedException)
            {
                return false;
            }

            return true;
        }

        private bool TryCreateTableVersionRecord(int version, string etag, out SiloInstanceRecord entry)
        {
            int etagInt;
            if (etag is null)
            {
                etagInt = 0;
            }
            else
            {
                if (!int.TryParse(etag, out etagInt))
                {
                    entry = default;
                    return false;
                }
            }

            entry = new SiloInstanceRecord
            {
                DeploymentId = clusterId,
                SiloIdentity = SiloInstanceRecord.TABLE_VERSION_ROW,
                MembershipVersion = version,
                ETag = etagInt
            };

            return true;
        }

        public async Task DeleteMembershipTableEntries(string clusterId)
        {
            try
            {
                var keys = new Dictionary<string, AttributeValue> { { $":{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", new AttributeValue(clusterId) } };
                var records = await storage.QueryAsync(this.options.TableName, keys, $"{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME} = :{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", item => new SiloInstanceRecord(item));

                var toDelete = new List<Dictionary<string, AttributeValue>>();
                foreach (var record in records.results)
                {
                    toDelete.Add(record.GetKeys());
                }

                List<Task> tasks = new List<Task>();
                foreach (var batch in toDelete.BatchIEnumerable(MAX_BATCH_SIZE))
                {
                    tasks.Add(storage.DeleteEntriesAsync(this.options.TableName, batch));
                }
                await Task.WhenAll(tasks);
            }
            catch (Exception exc)
            {
                this.logger.LogError(
                    (int)ErrorCode.MembershipBase,
                    exc,
                    "Unable to delete membership records on table {TableName} for ClusterId {ClusterId}",
                    this.options.TableName,
                    clusterId);
                throw;
            }
        }

        public async Task<MembershipTableData> ReadRow(SiloAddress siloAddress)
        {
            try
            {
                var siloEntryKeys = new Dictionary<string, AttributeValue>
                {
                    { $"{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", new AttributeValue(this.clusterId) },
                    { $"{SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME}", new AttributeValue(SiloInstanceRecord.ConstructSiloIdentity(siloAddress)) }
                };

                var versionEntryKeys = new Dictionary<string, AttributeValue>
                {
                    { $"{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", new AttributeValue(this.clusterId) },
                    { $"{SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME}", new AttributeValue(SiloInstanceRecord.TABLE_VERSION_ROW) }
                };

                var entries = await storage.GetEntriesTxAsync(this.options.TableName,
                    new[] {siloEntryKeys, versionEntryKeys}, fields => new SiloInstanceRecord(fields));

                MembershipTableData data = Convert(entries.ToList());
                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.LogTrace("Read my entry {SiloAddress} Table: {TableData}", siloAddress.ToString(), data.ToString());
                return data;
            }
            catch (Exception exc)
            {
                this.logger.LogWarning(
                    (int)ErrorCode.MembershipBase,
                    exc,
                    "Intermediate error reading silo entry for key {SiloAddress} from the table {TableName}",
                    siloAddress.ToString(),
                    this.options.TableName);
                throw;
            }
        }

        public async Task<MembershipTableData> ReadAll()
        {
            try
            {
                //first read just the version row so that we can check for version consistency
                var versionEntryKeys = new Dictionary<string, AttributeValue>
                {
                    { $"{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", new AttributeValue(this.clusterId) },
                    { $"{SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME}", new AttributeValue(SiloInstanceRecord.TABLE_VERSION_ROW) }
                };
                var versionRow = await this.storage.ReadSingleEntryAsync(this.options.TableName, versionEntryKeys,
                    fields => new SiloInstanceRecord(fields));
                if (versionRow == null)
                {
                    throw new KeyNotFoundException("No version row found for membership table");
                }

                var keys = new Dictionary<string, AttributeValue> { { $":{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", new AttributeValue(this.clusterId) } };
                var records = await this.storage.QueryAllAsync(this.options.TableName, keys, $"{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME} = :{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", item => new SiloInstanceRecord(item));

                if (records.Any(record => record.MembershipVersion > versionRow.MembershipVersion))
                {
                    this.logger.LogWarning((int)ErrorCode.MembershipBase, "Found an inconsistency while reading all silo entries");
                    //not expecting this to hit often, but if it does, should put in a limit
                    return await this.ReadAll();
                }

                MembershipTableData data = Convert(records);
                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.LogTrace("ReadAll Table {Table}", data.ToString());

                return data;
            }
            catch (Exception exc)
            {
                this.logger.LogWarning(
                    (int)ErrorCode.MembershipBase,
                    exc,
                    "Intermediate error reading all silo entries {TableName}.",
                    options.TableName);
                throw;
            }
        }

        public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            try
            {
                if (this.logger.IsEnabled(LogLevel.Debug)) this.logger.LogDebug("InsertRow entry = {Entry}", entry.ToString());
                var tableEntry = Convert(entry, tableVersion);

                if (!TryCreateTableVersionRecord(tableVersion.Version, tableVersion.VersionEtag, out var versionEntry))
                {
                    this.logger.LogWarning(
                        (int)ErrorCode.MembershipBase,
                        "Insert failed. Invalid ETag value. Will retry. Entry {Entry}, eTag {ETag}",
                        entry.ToString(),
                        tableVersion.VersionEtag);
                    return false;
                }

                versionEntry.ETag++;

                bool result;

                try
                {
                    var notExistConditionExpression =
                        $"attribute_not_exists({SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}) AND attribute_not_exists({SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME})";
                    var tableEntryInsert = new Put
                    {
                        Item = tableEntry.GetFields(true),
                        ConditionExpression = notExistConditionExpression,
                        TableName = this.options.TableName
                    };

                    var conditionalValues = new Dictionary<string, AttributeValue> { { CURRENT_ETAG_ALIAS, new AttributeValue { N = tableVersion.VersionEtag } } };
                    var etagConditionalExpression = $"{SiloInstanceRecord.ETAG_PROPERTY_NAME} = {CURRENT_ETAG_ALIAS}";
                    var versionEntryUpdate = new Update
                    {
                        TableName = this.options.TableName,
                        Key = versionEntry.GetKeys(),
                        ConditionExpression = etagConditionalExpression
                    };
                    (versionEntryUpdate.UpdateExpression, versionEntryUpdate.ExpressionAttributeValues) =
                        this.storage.ConvertUpdate(versionEntry.GetFields(), conditionalValues);

                    await this.storage.WriteTxAsync(new[] {tableEntryInsert}, new[] {versionEntryUpdate});

                    result = true;
                }
                catch (TransactionCanceledException canceledException)
                {
                    if (canceledException.Message.Contains("ConditionalCheckFailed")) //not a good way to check for this currently
                    {
                        result = false;
                        this.logger.LogWarning(
                            (int)ErrorCode.MembershipBase,
                            "Insert failed due to contention on the table. Will retry. Entry {Entry}",
                            entry.ToString());
                    }
                    else
                    {
                        throw;
                    }
                }

                return result;
            }
            catch (Exception exc)
            {
                this.logger.LogWarning(
                    (int)ErrorCode.MembershipBase,
                    exc,
                    "Intermediate error inserting entry {Entry} to the table {TableName}.",
                    entry.ToString(),
                    this.options.TableName);
                throw;
            }
        }

        public async Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            try
            {
                if (this.logger.IsEnabled(LogLevel.Debug)) this.logger.LogDebug("UpdateRow entry = {Entry}, etag = {}", entry.ToString(), etag);
                var siloEntry = Convert(entry, tableVersion);
                if (!int.TryParse(etag, out var currentEtag))
                {
                    this.logger.LogWarning(
                        (int)ErrorCode.MembershipBase,
                        "Update failed. Invalid ETag value. Will retry. Entry {Entry}, eTag {ETag}",
                        entry.ToString(),
                        etag);
                    return false;
                }

                siloEntry.ETag = currentEtag + 1;

                if (!TryCreateTableVersionRecord(tableVersion.Version, tableVersion.VersionEtag, out var versionEntry))
                {
                    this.logger.LogWarning(
                        (int)ErrorCode.MembershipBase,
                        "Update failed. Invalid ETag value. Will retry. Entry {Entry}, eTag {ETag}",
                        entry.ToString(),
                        tableVersion.VersionEtag);
                    return false;
                }

                versionEntry.ETag++;

                bool result;

                try
                {
                    var etagConditionalExpression = $"{SiloInstanceRecord.ETAG_PROPERTY_NAME} = {CURRENT_ETAG_ALIAS}";

                    var siloConditionalValues = new Dictionary<string, AttributeValue> { { CURRENT_ETAG_ALIAS, new AttributeValue { N = etag } } };
                    var siloEntryUpdate = new Update
                    {
                        TableName = this.options.TableName,
                        Key = siloEntry.GetKeys(),
                        ConditionExpression = etagConditionalExpression
                    };
                    (siloEntryUpdate.UpdateExpression, siloEntryUpdate.ExpressionAttributeValues) =
                        this.storage.ConvertUpdate(siloEntry.GetFields(), siloConditionalValues);


                    var versionConditionalValues = new Dictionary<string, AttributeValue> { { CURRENT_ETAG_ALIAS, new AttributeValue { N = tableVersion.VersionEtag } } };
                    var versionEntryUpdate = new Update
                    {
                        TableName = this.options.TableName,
                        Key = versionEntry.GetKeys(),
                        ConditionExpression = etagConditionalExpression
                    };
                    (versionEntryUpdate.UpdateExpression, versionEntryUpdate.ExpressionAttributeValues) =
                        this.storage.ConvertUpdate(versionEntry.GetFields(), versionConditionalValues);

                    await this.storage.WriteTxAsync(updates: new[] {siloEntryUpdate, versionEntryUpdate});
                    result = true;
                }
                catch (TransactionCanceledException canceledException)
                {
                    if (canceledException.Message.Contains("ConditionalCheckFailed")) //not a good way to check for this currently
                    {
                        result = false;
                        this.logger.LogWarning(
                            (int)ErrorCode.MembershipBase,
                            canceledException,
                            "Update failed due to contention on the table. Will retry. Entry {Entry}, eTag {ETag}",
                            entry.ToString(),
                            etag);
                    }
                    else
                    {
                        throw;
                    }
                }

                return result;
            }
            catch (Exception exc)
            {
                this.logger.LogWarning(
                    (int)ErrorCode.MembershipBase,
                    exc,
                    "Intermediate error updating entry {Entry} to the table {TableName}.",
                    entry.ToString(),
                    this.options.TableName);
                throw;
            }
        }

        public async Task UpdateIAmAlive(MembershipEntry entry)
        {
            try
            {
                if (this.logger.IsEnabled(LogLevel.Debug)) this.logger.LogDebug("Merge entry = {Entry}", entry.ToString());
                var siloEntry = ConvertPartial(entry);
                var fields = new Dictionary<string, AttributeValue> { { SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME, new AttributeValue(siloEntry.IAmAliveTime) } };
                var expression = $"attribute_exists({SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}) AND attribute_exists({SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME})";
                await this.storage.UpsertEntryAsync(this.options.TableName, siloEntry.GetKeys(),fields, expression);
            }
            catch (Exception exc)
            {
                this.logger.LogWarning(
                    (int)ErrorCode.MembershipBase,
                    exc,
                    "Intermediate error updating IAmAlive field for entry {Entry} to the table {TableName}.",
                    entry.ToString(),
                    this.options.TableName);
                throw;
            }
        }

        private MembershipTableData Convert(List<SiloInstanceRecord> entries)
        {
            try
            {
                var memEntries = new List<Tuple<MembershipEntry, string>>();
                var tableVersion = NotFoundTableVersion;
                foreach (var tableEntry in entries)
                {
                    if (tableEntry.SiloIdentity == SiloInstanceRecord.TABLE_VERSION_ROW)
                    {
                        tableVersion = new TableVersion(tableEntry.MembershipVersion, tableEntry.ETag.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        try
                        {
                            MembershipEntry membershipEntry = Parse(tableEntry);
                            memEntries.Add(new Tuple<MembershipEntry, string>(membershipEntry,
                                tableEntry.ETag.ToString(CultureInfo.InvariantCulture)));
                        }
                        catch (Exception exc)
                        {
                            this.logger.LogError(
                                (int)ErrorCode.MembershipBase,
                                exc,
                                "Intermediate error parsing SiloInstanceTableEntry to MembershipTableData: {TableEntry}. Ignoring this entry.",
                                tableEntry);
                        }
                    }
                }
                var data = new MembershipTableData(memEntries, tableVersion);
                return data;
            }
            catch (Exception exc)
            {
                this.logger.LogError(
                    (int)ErrorCode.MembershipBase,
                    exc,
                    "Intermediate error parsing SiloInstanceTableEntry to MembershipTableData: {Entries}.",
                    Utils.EnumerableToString(entries));
                throw;
            }
        }

        private MembershipEntry Parse(SiloInstanceRecord tableEntry)
        {
            var parse = new MembershipEntry
            {
                HostName = tableEntry.HostName,
                Status = (SiloStatus)tableEntry.Status
            };

            parse.ProxyPort = tableEntry.ProxyPort;

            parse.SiloAddress = SiloAddress.New(IPAddress.Parse(tableEntry.Address), tableEntry.Port, tableEntry.Generation);

            if (!string.IsNullOrEmpty(tableEntry.SiloName))
            {
                parse.SiloName = tableEntry.SiloName;
            }

            parse.StartTime = !string.IsNullOrEmpty(tableEntry.StartTime) ?
                LogFormatter.ParseDate(tableEntry.StartTime) : default;

            parse.IAmAliveTime = !string.IsNullOrEmpty(tableEntry.IAmAliveTime) ?
                LogFormatter.ParseDate(tableEntry.IAmAliveTime) : default;

            var suspectingSilos = new List<SiloAddress>();
            var suspectingTimes = new List<DateTime>();

            if (!string.IsNullOrEmpty(tableEntry.SuspectingSilos))
            {
                string[] silos = tableEntry.SuspectingSilos.Split('|');
                foreach (string silo in silos)
                {
                    suspectingSilos.Add(SiloAddress.FromParsableString(silo));
                }
            }

            if (!string.IsNullOrEmpty(tableEntry.SuspectingTimes))
            {
                string[] times = tableEntry.SuspectingTimes.Split('|');
                foreach (string time in times)
                    suspectingTimes.Add(LogFormatter.ParseDate(time));
            }

            if (suspectingSilos.Count != suspectingTimes.Count)
                throw new OrleansException($"SuspectingSilos.Length of {suspectingSilos.Count} as read from Azure table is not equal to SuspectingTimes.Length of {suspectingTimes.Count}");

            for (int i = 0; i < suspectingSilos.Count; i++)
                parse.AddSuspector(suspectingSilos[i], suspectingTimes[i]);

            return parse;
        }

        private SiloInstanceRecord Convert(MembershipEntry memEntry, TableVersion tableVersion)
        {
            var tableEntry = new SiloInstanceRecord
            {
                DeploymentId = this.clusterId,
                Address = memEntry.SiloAddress.Endpoint.Address.ToString(),
                Port = memEntry.SiloAddress.Endpoint.Port,
                Generation = memEntry.SiloAddress.Generation,
                HostName = memEntry.HostName,
                Status = (int)memEntry.Status,
                ProxyPort = memEntry.ProxyPort,
                SiloName = memEntry.SiloName,
                StartTime = LogFormatter.PrintDate(memEntry.StartTime),
                IAmAliveTime = LogFormatter.PrintDate(memEntry.IAmAliveTime),
                SiloIdentity = SiloInstanceRecord.ConstructSiloIdentity(memEntry.SiloAddress),
                MembershipVersion = tableVersion.Version
            };

            if (memEntry.SuspectTimes != null)
            {
                var siloList = new StringBuilder();
                var timeList = new StringBuilder();
                bool first = true;
                foreach (var tuple in memEntry.SuspectTimes)
                {
                    if (!first)
                    {
                        siloList.Append('|');
                        timeList.Append('|');
                    }
                    siloList.Append(tuple.Item1.ToParsableString());
                    timeList.Append(LogFormatter.PrintDate(tuple.Item2));
                    first = false;
                }

                tableEntry.SuspectingSilos = siloList.ToString();
                tableEntry.SuspectingTimes = timeList.ToString();
            }
            else
            {
                tableEntry.SuspectingSilos = string.Empty;
                tableEntry.SuspectingTimes = string.Empty;
            }

            return tableEntry;
        }

        private SiloInstanceRecord ConvertPartial(MembershipEntry memEntry)
        {
            return new SiloInstanceRecord
            {
                DeploymentId = this.clusterId,
                IAmAliveTime = LogFormatter.PrintDate(memEntry.IAmAliveTime),
                SiloIdentity = SiloInstanceRecord.ConstructSiloIdentity(memEntry.SiloAddress)
            };
        }

        public async Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
        {
            try
            {
                var keys = new Dictionary<string, AttributeValue>
                {
                    { $":{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", new AttributeValue(this.clusterId) },
                };
                var filter = $"{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME} = :{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}";

                var records = await this.storage.QueryAllAsync(this.options.TableName, keys, filter, item => new SiloInstanceRecord(item));
                var defunctRecordKeys = records.Where(r => SiloIsDefunct(r, beforeDate)).Select(r => r.GetKeys());

                var tasks = new List<Task>();
                foreach (var batch in defunctRecordKeys.BatchIEnumerable(MAX_BATCH_SIZE))
                {
                    tasks.Add(this.storage.DeleteEntriesAsync(this.options.TableName, batch));
                }
                await Task.WhenAll(tasks);
            }
            catch (Exception exc)
            {
                this.logger.LogError(
                    (int)ErrorCode.MembershipBase,
                    exc,
                    "Unable to clean up defunct membership records on table {TableName} for ClusterId {ClusterId}",
                    this.options.TableName,
                    this.clusterId);
                throw;
            }
        }

        private static bool SiloIsDefunct(SiloInstanceRecord silo, DateTimeOffset beforeDate)
        {
            return DateTimeOffset.TryParse(silo.IAmAliveTime, out var iAmAliveTime)
                    && iAmAliveTime < beforeDate
                    && silo.Status != (int)SiloStatus.Active;
        }
    }
}
