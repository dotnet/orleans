using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Clustering.DynamoDB
{
    internal partial class DynamoDBMembershipTable : IMembershipTable
    {
        private const string CURRENT_ETAG_ALIAS = ":currentETag";
        private const string MEMBERSHIP_DATE_FORMAT = "yyyy-MM-dd HH:mm:ss.fff 'GMT'";
        private const int MAX_BATCH_SIZE = 25;
        private const int MAX_CONCURRENT_CLEANUP_DELETES = 25;

        private readonly ILogger logger;
        private DynamoDBStorage storage = null!;
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

        [Obsolete("Use InitializeMembershipTableAsync instead.")]
        public Task InitializeMembershipTable(bool tryInitTableVersion) => InitializeMembershipTableAsync(tryInitTableVersion, CancellationToken.None);

        public async Task InitializeMembershipTableAsync(bool tryInitTableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

            LogInformationInitializingMembershipTable();
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
                },
                cancellationToken: cancellationToken);

            // even if I am not the one who created the table,
            // try to insert an initial table version if it is not already there,
            // so we always have a first table version row, before this silo starts working.
            if (tryInitTableVersion)
            {
                // ignore return value, since we don't care if I inserted it or not, as long as it is in there.
                bool created = await TryCreateTableVersionEntryAsync(cancellationToken);
                if (created) LogInformationCreatedNewTableVersionRow();
            }
        }

        private async Task<bool> TryCreateTableVersionEntryAsync(CancellationToken cancellationToken)
        {
            var keys = new Dictionary<string, AttributeValue>
            {
                { $"{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", new AttributeValue(this.clusterId) },
                { $"{SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME}", new AttributeValue(SiloInstanceRecord.TABLE_VERSION_ROW) }
            };

            var versionRow = await storage.ReadSingleEntryAsync(this.options.TableName, keys, ParseRecord, cancellationToken);
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
                await storage.PutEntryAsync(this.options.TableName, entry.GetFields(true), cancellationToken, notExistConditionExpression);
            }
            catch (ConditionalCheckFailedException)
            {
                return false;
            }

            return true;
        }

        private bool TryCreateTableVersionRecord(int version, string? etag, [NotNullWhen(true)] out SiloInstanceRecord? entry)
        {
            int etagInt;
            if (etag is null)
            {
                etagInt = 0;
            }
            else
            {
                if (!int.TryParse(etag, NumberStyles.Integer, CultureInfo.InvariantCulture, out etagInt))
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

        [Obsolete("Use DeleteMembershipTableEntriesAsync instead.")]
        public Task DeleteMembershipTableEntries(string clusterId) => DeleteMembershipTableEntriesAsync(clusterId, CancellationToken.None);

        public async Task DeleteMembershipTableEntriesAsync(string clusterId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var keys = new Dictionary<string, AttributeValue> { { $":{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", new AttributeValue(clusterId) } };
                var records = await storage.QueryAllAsync(this.options.TableName, keys, $"{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME} = :{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", item => new SiloInstanceRecord(item), cancellationToken);

                var toDelete = new List<Dictionary<string, AttributeValue>>();
                foreach (var record in records)
                {
                    toDelete.Add(record.GetKeys());
                }

                // Capture synchronous cancellation as a task so every started batch remains owned by WhenAll.
                await Task.WhenAll(toDelete.BatchIEnumerable(MAX_BATCH_SIZE)
                    .Select(async batch => await storage.DeleteEntriesAsync(this.options.TableName, batch, cancellationToken)));
            }
            catch (Exception exc)
            {
                LogErrorUnableToDeleteMembershipRecords(exc, this.options.TableName, clusterId);
                throw;
            }
        }

        [Obsolete("Use ReadRowAsync instead.")]
        public Task<MembershipTableData> ReadRow(SiloAddress siloAddress) => ReadRowAsync(siloAddress, CancellationToken.None);

        public async Task<MembershipTableData> ReadRowAsync(SiloAddress siloAddress, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                    new[] { siloEntryKeys, versionEntryKeys }, ParseRecord, cancellationToken);

                MembershipTableData data = Convert(entries.ToList());
                LogTraceReadMyEntry(siloAddress, data);
                return data;
            }
            catch (Exception exc)
            {
                LogWarningIntermediateErrorReadingSiloEntry(exc, siloAddress, this.options.TableName);
                throw;
            }
        }

        [Obsolete("Use ReadAllAsync instead.")]
        public Task<MembershipTableData> ReadAll() => ReadAllAsync(CancellationToken.None);

        public async Task<MembershipTableData> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                //first read just the version row so that we can check for version consistency
                var versionEntryKeys = new Dictionary<string, AttributeValue>
                {
                    { $"{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", new AttributeValue(this.clusterId) },
                    { $"{SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME}", new AttributeValue(SiloInstanceRecord.TABLE_VERSION_ROW) }
                };
                var keys = new Dictionary<string, AttributeValue> { { $":{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", new AttributeValue(this.clusterId) } };
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var versionRow = await this.storage.ReadSingleEntryAsync(this.options.TableName, versionEntryKeys,
                        ParseRecord, cancellationToken);
                    if (versionRow == null)
                    {
                        throw new KeyNotFoundException("No version row found for membership table");
                    }

                    var records = await this.storage.QueryAllAsync(this.options.TableName, keys, $"{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME} = :{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", ParseRecord, cancellationToken);

                    var versionAfter = await this.storage.ReadSingleEntryAsync(this.options.TableName, versionEntryKeys,
                        ParseRecord, cancellationToken);
                    if (versionAfter is null
                        || versionAfter.ETag != versionRow.ETag
                        || !records.Exists(record => record.SiloIdentity == SiloInstanceRecord.TABLE_VERSION_ROW
                            && record.ETag == versionRow.ETag))
                    {
                        LogWarningFoundInconsistencyReadingAllSiloEntries();
                        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                        continue;
                    }

                    MembershipTableData data = Convert(records);
                    LogTraceReadAllTable(data);
                    return data;
                }
            }
            catch (Exception exc)
            {
                LogWarningIntermediateErrorReadingAllSiloEntries(exc, options.TableName);
                throw;
            }
        }

        [Obsolete("Use InsertRowAsync instead.")]
        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => InsertRowAsync(entry, tableVersion, CancellationToken.None);

        public async Task<bool> InsertRowAsync(MembershipEntry entry, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                LogDebugInsertRow(entry);
                var tableEntry = Convert(entry, tableVersion);

                if (!TryCreateTableVersionRecord(tableVersion.Version, tableVersion.VersionEtag, out var versionEntry))
                {
                    LogWarningInsertFailedInvalidETag(entry, tableVersion.VersionEtag);
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

                    await this.storage.WriteTxAsync(cancellationToken, new[] { tableEntryInsert }, new[] { versionEntryUpdate });

                    result = true;
                }
                catch (TransactionCanceledException canceledException) when (IsContention(canceledException))
                {
                    result = false;
                    LogWarningInsertFailedDueToContention(entry);
                }

                return result;
            }
            catch (Exception exc)
            {
                LogWarningIntermediateErrorInsertingEntry(exc, entry, this.options.TableName);
                throw;
            }
        }

        [Obsolete("Use UpdateRowAsync instead.")]
        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => UpdateRowAsync(entry, etag, tableVersion, CancellationToken.None);

        public async Task<bool> UpdateRowAsync(MembershipEntry entry, string etag, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                LogDebugUpdateRow(entry, etag);
                var siloEntry = Convert(entry, tableVersion);
                if (!int.TryParse(etag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var currentEtag))
                {
                    LogWarningUpdateFailedInvalidETag(entry, etag);
                    return false;
                }

                siloEntry.ETag = currentEtag + 1;

                var current = await storage.ReadSingleEntryAsync(this.options.TableName, siloEntry.GetKeys(),
                    fields => new SiloInstanceRecord(fields), cancellationToken);
                if (current is null || current.ETag != currentEtag)
                {
                    return false;
                }

                if (!string.IsNullOrEmpty(current.IAmAliveTime)
                    && LogFormatter.ParseDate(current.IAmAliveTime) > entry.IAmAliveTime)
                {
                    siloEntry.IAmAliveTime = current.IAmAliveTime;
                }

                if (!TryCreateTableVersionRecord(tableVersion.Version, tableVersion.VersionEtag, out var versionEntry))
                {
                    LogWarningUpdateFailedInvalidETag(entry, tableVersion.VersionEtag);
                    return false;
                }

                versionEntry.ETag++;

                bool result;

                try
                {
                    var etagConditionalExpression = $"{SiloInstanceRecord.ETAG_PROPERTY_NAME} = {CURRENT_ETAG_ALIAS}";

                    var siloConditionalValues = new Dictionary<string, AttributeValue> { { CURRENT_ETAG_ALIAS, new AttributeValue { N = etag } } };
                    var heartbeatCondition = $"attribute_not_exists({SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME})";
                    if (current.IAmAliveTime is not null)
                    {
                        heartbeatCondition = $"{SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME} = :currentHeartbeat";
                        siloConditionalValues.Add(":currentHeartbeat", new AttributeValue(current.IAmAliveTime));
                    }

                    // Compare the independently updated heartbeat to preserve its maximum when replacing the row.
                    var siloEntryUpdate = new Put
                    {
                        TableName = this.options.TableName,
                        Item = siloEntry.GetFields(includeKeys: true),
                        ConditionExpression = $"{etagConditionalExpression} AND {heartbeatCondition}",
                        ExpressionAttributeValues = siloConditionalValues
                    };

                    var versionConditionalValues = new Dictionary<string, AttributeValue> { { CURRENT_ETAG_ALIAS, new AttributeValue { N = tableVersion.VersionEtag } } };
                    var versionEntryUpdate = new Update
                    {
                        TableName = this.options.TableName,
                        Key = versionEntry.GetKeys(),
                        ConditionExpression = etagConditionalExpression
                    };
                    (versionEntryUpdate.UpdateExpression, versionEntryUpdate.ExpressionAttributeValues) =
                        this.storage.ConvertUpdate(versionEntry.GetFields(), versionConditionalValues);

                    await this.storage.WriteTxAsync(cancellationToken, puts: new[] { siloEntryUpdate }, updates: new[] { versionEntryUpdate });
                    result = true;
                }
                catch (TransactionCanceledException canceledException) when (IsContention(canceledException))
                {
                    result = false;
                    LogWarningUpdateFailedDueToContention(canceledException, entry, etag);
                }

                return result;
            }
            catch (Exception exc)
            {
                LogWarningIntermediateErrorUpdatingEntry(exc, entry, this.options.TableName);
                throw;
            }
        }

        [Obsolete("Use UpdateIAmAliveAsync instead.")]
        public Task UpdateIAmAlive(MembershipEntry entry) => UpdateIAmAliveAsync(entry, CancellationToken.None);

        public async Task UpdateIAmAliveAsync(MembershipEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                LogDebugMergeEntry(entry);
                var siloEntry = ConvertPartial(entry);
                var fields = new Dictionary<string, AttributeValue> { { SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME, new AttributeValue(siloEntry.IAmAliveTime) } };
                var expression = $"attribute_exists({SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME})"
                    + $" AND attribute_exists({SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME})"
                    + $" AND (attribute_not_exists({SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME})"
                    + $" OR {SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME} < :{SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME})";
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await this.storage.UpsertEntryAsync(this.options.TableName, siloEntry.GetKeys(), fields, cancellationToken, expression);
                        break;
                    }
                    catch (ConditionalCheckFailedException)
                    {
                        var current = await storage.ReadSingleEntryAsync(this.options.TableName, siloEntry.GetKeys(),
                            values => new SiloInstanceRecord(values), cancellationToken);
                        if (current is null)
                        {
                            var versionKeys = new Dictionary<string, AttributeValue>
                            {
                                [SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME] = new AttributeValue(this.clusterId),
                                [SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME] = new AttributeValue(SiloInstanceRecord.TABLE_VERSION_ROW)
                            };
                            _ = await storage.ReadSingleEntryAsync(this.options.TableName, versionKeys,
                                ParseRecord, cancellationToken)
                                ?? throw new KeyNotFoundException("No version row found for membership table");
                            break;
                        }

                        if (!string.IsNullOrEmpty(current.IAmAliveTime)
                            && LogFormatter.ParseDate(current.IAmAliveTime) >= LogFormatter.ParseDate(siloEntry.IAmAliveTime!))
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception exc)
            {
                LogWarningIntermediateErrorUpdatingIAmAlive(exc, entry, this.options.TableName);
                throw;
            }
        }

        private static SiloInstanceRecord ParseRecord(Dictionary<string, AttributeValue> fields)
        {
            if (fields[SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME].S == SiloInstanceRecord.TABLE_VERSION_ROW)
            {
                ValidateVersionAttribute(SiloInstanceRecord.MEMBERSHIP_VERSION_PROPERTY_NAME);
                ValidateVersionAttribute(SiloInstanceRecord.ETAG_PROPERTY_NAME);
            }

            return new SiloInstanceRecord(fields);

            void ValidateVersionAttribute(string attribute)
            {
                if (!fields.TryGetValue(attribute, out var value)
                    || !int.TryParse(value.N, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    throw new FormatException($"Membership table version row has a missing or invalid {attribute} attribute.");
                }
            }
        }

        private MembershipTableData Convert(List<SiloInstanceRecord> entries)
        {
            try
            {
                var memEntries = new List<Tuple<MembershipEntry, string>>();
                TableVersion? tableVersion = null;
                foreach (var tableEntry in entries)
                {
                    if (string.Equals(tableEntry.SiloIdentity, SiloInstanceRecord.TABLE_VERSION_ROW, StringComparison.Ordinal))
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
                            LogErrorIntermediateErrorParsingSiloInstanceTableEntry(exc, tableEntry);
                            throw;
                        }
                    }
                }
                var data = new MembershipTableData(memEntries,
                    tableVersion ?? throw new KeyNotFoundException("No version row found for membership table"));
                return data;
            }
            catch (Exception exc)
            {
                LogErrorIntermediateErrorParsingSiloInstanceTableEntries(exc, entries);
                throw;
            }
        }

        private static MembershipEntry Parse(SiloInstanceRecord tableEntry)
        {
            var parse = new MembershipEntry
            {
                // Persisted silo membership records always include a host name.
                HostName = tableEntry.HostName!,
                Status = (SiloStatus)tableEntry.Status
            };

            parse.ProxyPort = tableEntry.ProxyPort;

            parse.SiloAddress = SiloAddress.New(IPAddress.Parse(tableEntry.Address!), tableEntry.Port, tableEntry.Generation);

            if (!string.IsNullOrEmpty(tableEntry.SiloName))
            {
                parse.SiloName = tableEntry.SiloName;
            }

            parse.StartTime = !string.IsNullOrEmpty(tableEntry.StartTime) ?
                LogFormatter.ParseDate(tableEntry.StartTime) : default;

            parse.IAmAliveTime = !string.IsNullOrEmpty(tableEntry.IAmAliveTime) ?
                LogFormatter.ParseDate(tableEntry.IAmAliveTime) : default;

            foreach (var (silo, time) in ParseSuspectTimes(tableEntry))
            {
                parse.AddSuspector(silo, time);
            }

            return parse;
        }

        private static List<(SiloAddress Silo, DateTime Time)> ParseSuspectTimes(SiloInstanceRecord record)
        {
            var silos = string.IsNullOrEmpty(record.SuspectingSilos) ? [] : record.SuspectingSilos.Split('|');
            var times = string.IsNullOrEmpty(record.SuspectingTimes) ? [] : record.SuspectingTimes.Split('|');
            if (silos.Length != times.Length)
            {
                throw new OrleansException($"SuspectingSilos.Length of {silos.Length} as read from DynamoDB is not equal to SuspectingTimes.Length of {times.Length} for silo '{record.SiloIdentity}'.");
            }

            var result = new List<(SiloAddress, DateTime)>(silos.Length);
            for (var i = 0; i < silos.Length; i++)
            {
                result.Add((SiloAddress.FromParsableString(silos[i]), LogFormatter.ParseDate(times[i])));
            }

            return result;
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

        [Obsolete("Use CleanupDefunctSiloEntriesAsync instead.")]
        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => CleanupDefunctSiloEntriesAsync(beforeDate, CancellationToken.None);

        public async Task CleanupDefunctSiloEntriesAsync(DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var keys = new Dictionary<string, AttributeValue>
                {
                    { $":{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", new AttributeValue(this.clusterId) },
                };
                var filter = $"{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME} = :{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}";

                var records = await this.storage.QueryAllAsync(this.options.TableName, keys, filter, item => item, cancellationToken);
                foreach (var batch in records.Where(fields => SiloIsDefunct(new SiloInstanceRecord(fields), beforeDate))
                    .BatchIEnumerable(MAX_CONCURRENT_CLEANUP_DELETES))
                {
                    await Task.WhenAll(batch.Select(DeleteDefunctEntry));
                }

                async Task DeleteDefunctEntry(Dictionary<string, AttributeValue> fields)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var conditions = new List<string>();
                    var values = new Dictionary<string, AttributeValue>();
                    foreach (var attribute in new[]
                    {
                        SiloInstanceRecord.STATUS_PROPERTY_NAME,
                        SiloInstanceRecord.ETAG_PROPERTY_NAME,
                        SiloInstanceRecord.START_TIME_PROPERTY_NAME,
                        SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME,
                        SiloInstanceRecord.SUSPECTING_SILOS_PROPERTY_NAME,
                        SiloInstanceRecord.SUSPECTING_TIMES_PROPERTY_NAME
                    })
                    {
                        if (fields.TryGetValue(attribute, out var value))
                        {
                            conditions.Add($"{attribute} = :{attribute}");
                            values.Add($":{attribute}", value);
                        }
                        else
                        {
                            conditions.Add($"attribute_not_exists({attribute})");
                        }
                    }

                    try
                    {
                        await this.storage.DeleteEntryAsync(this.options.TableName, new SiloInstanceRecord(fields).GetKeys(),
                            cancellationToken, string.Join(" AND ", conditions), values);
                    }
                    catch (ConditionalCheckFailedException)
                    {
                        // A changed row is reconsidered by the next cleanup.
                    }
                }
            }
            catch (Exception exc)
            {
                LogErrorUnableToCleanUpDefunctMembershipRecords(exc, this.options.TableName, this.clusterId);
                throw;
            }
        }

        internal static bool SiloIsDefunct(SiloInstanceRecord silo, DateTimeOffset beforeDate)
        {
            return silo.Status == (int)SiloStatus.Dead
                && !string.IsNullOrEmpty(silo.IAmAliveTime)
                && IsBeforeCutoff(silo.IAmAliveTime)
                && IsBeforeCutoff(silo.StartTime)
                && ParseSuspectTimes(silo).All(vote => vote.Time < beforeDate.UtcDateTime);

            bool IsBeforeCutoff(string? value) => string.IsNullOrEmpty(value)
                || (DateTimeOffset.TryParseExact(
                    value,
                    MEMBERSHIP_DATE_FORMAT,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var timestamp)
                    && timestamp < beforeDate);
        }

        private static bool IsContention(TransactionCanceledException exception) =>
            exception.CancellationReasons is { Count: > 0 } reasons
            && reasons.All(reason => reason.Code is "None" or "ConditionalCheckFailed" or "TransactionConflict")
            && reasons.Any(reason => reason.Code is "ConditionalCheckFailed" or "TransactionConflict");

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Initializing AWS DynamoDB Membership Table"
        )]
        private partial void LogInformationInitializingMembershipTable();

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Created new table version row."
        )]
        private partial void LogInformationCreatedNewTableVersionRow();

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Unable to delete membership records on table {TableName} for ClusterId {ClusterId}"
        )]
        private partial void LogErrorUnableToDeleteMembershipRecords(Exception exception, string tableName, string clusterId);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Read my entry {SiloAddress} Table: {TableData}"
        )]
        private partial void LogTraceReadMyEntry(SiloAddress siloAddress, MembershipTableData tableData);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Intermediate error reading silo entry for key {SiloAddress} from the table {TableName}"
        )]
        private partial void LogWarningIntermediateErrorReadingSiloEntry(Exception exception, SiloAddress siloAddress, string tableName);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Found an inconsistency while reading all silo entries"
        )]
        private partial void LogWarningFoundInconsistencyReadingAllSiloEntries();

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "ReadAll Table {Table}"
        )]
        private partial void LogTraceReadAllTable(MembershipTableData table);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Intermediate error reading all silo entries {TableName}."
        )]
        private partial void LogWarningIntermediateErrorReadingAllSiloEntries(Exception exception, string tableName);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "InsertRow entry = {Entry}"
        )]
        private partial void LogDebugInsertRow(MembershipEntry entry);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Insert failed. Invalid ETag value. Will retry. Entry {Entry}, eTag {ETag}"
        )]
        private partial void LogWarningInsertFailedInvalidETag(MembershipEntry entry, string etag);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Insert failed due to contention on the table. Will retry. Entry {Entry}"
        )]
        private partial void LogWarningInsertFailedDueToContention(MembershipEntry entry);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Intermediate error inserting entry {Entry} to the table {TableName}."
        )]
        private partial void LogWarningIntermediateErrorInsertingEntry(Exception exception, MembershipEntry entry, string tableName);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "UpdateRow entry = {Entry}, etag = {Etag}"
        )]
        private partial void LogDebugUpdateRow(MembershipEntry entry, string etag);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Update failed. Invalid ETag value. Will retry. Entry {Entry}, eTag {ETag}"
        )]
        private partial void LogWarningUpdateFailedInvalidETag(MembershipEntry entry, string etag);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Update failed due to contention on the table. Will retry. Entry {Entry}, eTag {ETag}"
        )]
        private partial void LogWarningUpdateFailedDueToContention(Exception exception, MembershipEntry entry, string etag);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Intermediate error updating entry {Entry} to the table {TableName}."
        )]
        private partial void LogWarningIntermediateErrorUpdatingEntry(Exception exception, MembershipEntry entry, string tableName);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Merge entry = {Entry}"
        )]
        private partial void LogDebugMergeEntry(MembershipEntry entry);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Intermediate error updating IAmAlive field for entry {Entry} to the table {TableName}."
        )]
        private partial void LogWarningIntermediateErrorUpdatingIAmAlive(Exception exception, MembershipEntry entry, string tableName);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Intermediate error parsing SiloInstanceTableEntry to MembershipTableData: {TableEntry}. Ignoring this entry."
        )]
        private partial void LogErrorIntermediateErrorParsingSiloInstanceTableEntry(Exception exception, SiloInstanceRecord tableEntry);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Intermediate error parsing SiloInstanceTableEntry to MembershipTableData: {Entries}."
        )]
        private partial void LogErrorIntermediateErrorParsingSiloInstanceTableEntries(Exception exception, IEnumerable<SiloInstanceRecord> entries);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Unable to clean up defunct membership records on table {TableName} for ClusterId {ClusterId}"
        )]
        private partial void LogErrorUnableToCleanUpDefunctMembershipRecords(Exception exception, string tableName, string clusterId);
    }
}
