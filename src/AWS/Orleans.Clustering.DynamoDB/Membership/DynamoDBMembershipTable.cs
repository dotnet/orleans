using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Clustering.DynamoDB
{
    internal class DynamoDBMembershipTable : IMembershipTable
    {
        //DynamoDB does not support the extended Membership Protocol and will always return the same table version information
        private readonly TableVersion tableVersion = new TableVersion(0, "0");

        private const string CURRENT_ETAG_ALIAS = ":currentETag";
        private const int MAX_BATCH_SIZE = 25;

        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;
        private DynamoDBStorage storage;
        private readonly DynamoDBClusteringOptions options;
        private readonly string clusterId;

        public DynamoDBMembershipTable(
            ILoggerFactory loggerFactory, 
            IOptions<DynamoDBClusteringOptions> clusteringOptions, 
            IOptions<ClusterOptions> clusterOptions)
        {
            this.loggerFactory = loggerFactory;
            logger = loggerFactory.CreateLogger<DynamoDBMembershipTable>();
            this.options = clusteringOptions.Value;
            this.clusterId = clusterOptions.Value.ClusterId;
        }

        public Task InitializeMembershipTable(bool tryInitTableVersion)
        {
            this.storage = new DynamoDBStorage(this.loggerFactory, this.options.Service, this.options.AccessKey, this.options.SecretKey,
                  this.options.ReadCapacityUnits, this.options.WriteCapacityUnits);

            logger.Info(ErrorCode.MembershipBase, "Initializing AWS DynamoDB Membership Table");
            return storage.InitializeTable(this.options.TableName,
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
                this.logger.Error(ErrorCode.MembershipBase, string.Format("Unable to delete membership records on table {0} for clusterId {1}: Exception={2}",
                    this.options.TableName, clusterId, exc));
                throw;
            }
        }

        public async Task<MembershipTableData> ReadRow(SiloAddress siloAddress)
        {
            try
            {
                var keys = new Dictionary<string, AttributeValue>
                {
                    { $"{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", new AttributeValue(this.clusterId) },
                    { $"{SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME}", new AttributeValue(SiloInstanceRecord.ConstructSiloIdentity(siloAddress)) }
                };
                var entry = await storage.ReadSingleEntryAsync(this.options.TableName, keys, fields => new SiloInstanceRecord(fields));
                MembershipTableData data = entry != null ? Convert(new List<SiloInstanceRecord> { entry }) : new MembershipTableData(this.tableVersion);
                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace("Read my entry {0} Table=" + Environment.NewLine + "{1}", siloAddress.ToLongString(), data.ToString());
                return data;
            }
            catch (Exception exc)
            {
                this.logger.Warn(ErrorCode.MembershipBase,
                    $"Intermediate error reading silo entry for key {siloAddress.ToLongString()} from the table {this.options.TableName}.", exc);
                throw;
            }
        }

        public async Task<MembershipTableData> ReadAll()
        {
            try
            {
                var keys = new Dictionary<string, AttributeValue> { { $":{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", new AttributeValue(this.clusterId) } };
                var records = await this.storage.QueryAsync(this.options.TableName, keys, $"{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME} = :{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", item => new SiloInstanceRecord(item));

                MembershipTableData data = Convert(records.results);
                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace("ReadAll Table=" + Environment.NewLine + "{0}", data.ToString());

                return data;
            }
            catch (Exception exc)
            {
                this.logger.Warn(ErrorCode.MembershipBase,
                    $"Intermediate error reading all silo entries {this.options.TableName}.", exc);
                throw;
            }
        }

        public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            try
            {
                if (this.logger.IsEnabled(LogLevel.Debug)) this.logger.Debug("InsertRow entry = {0}", entry.ToFullString());
                var tableEntry = Convert(entry);

                bool result;

                try
                {
                    var expression = $"attribute_not_exists({SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}) AND attribute_not_exists({SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME})";

                    await this.storage.PutEntryAsync(this.options.TableName, tableEntry.GetFields(true), expression);
                    result = true;
                }
                catch (ConditionalCheckFailedException)
                {
                    result = false;
                    this.logger.Warn(ErrorCode.MembershipBase,
                        $"Insert failed due to contention on the table. Will retry. Entry {entry.ToFullString()}");
                }
                    
                return result;
            }
            catch (Exception exc)
            {
                this.logger.Warn(ErrorCode.MembershipBase,
                    $"Intermediate error inserting entry {entry.ToFullString()} to the table {this.options.TableName}.", exc);
                throw;
            }
        }

        public async Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            try
            {
                if (this.logger.IsEnabled(LogLevel.Debug)) this.logger.Debug("UpdateRow entry = {0}, etag = {1}", entry.ToFullString(), etag);
                var siloEntry = Convert(entry);
                int currentEtag = 0;
                if (!int.TryParse(etag, out currentEtag))
                {
                    this.logger.Warn(ErrorCode.MembershipBase,
                        $"Update failed. Invalid ETag value. Will retry. Entry {entry.ToFullString()}, eTag {etag}");
                    return false;
                }

                siloEntry.ETag = currentEtag + 1;

                bool result;

                try
                {
                    var conditionalValues = new Dictionary<string, AttributeValue> { { CURRENT_ETAG_ALIAS, new AttributeValue { N = etag } } };
                    var etagConditionalExpression = $"{SiloInstanceRecord.ETAG_PROPERTY_NAME} = {CURRENT_ETAG_ALIAS}";
                    await this.storage.UpsertEntryAsync(this.options.TableName, siloEntry.GetKeys(),
                        siloEntry.GetFields(), etagConditionalExpression, conditionalValues);

                    result = true;
                }
                catch (ConditionalCheckFailedException)
                {
                    result = false;
                    this.logger.Warn(ErrorCode.MembershipBase,
                        $"Update failed due to contention on the table. Will retry. Entry {entry.ToFullString()}, eTag {etag}");
                }
                    
                return result;
            }
            catch (Exception exc)
            {
                this.logger.Warn(ErrorCode.MembershipBase,
                    $"Intermediate error updating entry {entry.ToFullString()} to the table {this.options.TableName}.", exc);
                throw;
            }
        }

        public async Task UpdateIAmAlive(MembershipEntry entry)
        {
            try
            {
                if (this.logger.IsEnabled(LogLevel.Debug)) this.logger.Debug("Merge entry = {0}", entry.ToFullString());
                var siloEntry = ConvertPartial(entry);
                var fields = new Dictionary<string, AttributeValue> { { SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME, new AttributeValue(siloEntry.IAmAliveTime) } };
                var expression = $"attribute_exists({SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}) AND attribute_exists({SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME})";
                await this.storage.UpsertEntryAsync(this.options.TableName, siloEntry.GetKeys(),fields, expression);
            }
            catch (Exception exc)
            {
                this.logger.Warn(ErrorCode.MembershipBase,
                    $"Intermediate error updating IAmAlive field for entry {entry.ToFullString()} to the table {this.options.TableName}.", exc);
                throw;
            }
        }

        private MembershipTableData Convert(List<SiloInstanceRecord> entries)
        {
            try
            {
                var memEntries = new List<Tuple<MembershipEntry, string>>();

                foreach (var tableEntry in entries)
                {
                    try
                    {
                        MembershipEntry membershipEntry = Parse(tableEntry);
                        memEntries.Add(new Tuple<MembershipEntry, string>(membershipEntry, tableEntry.ETag.ToString()));
                    }
                    catch (Exception exc)
                    {
                        this.logger.Error(ErrorCode.MembershipBase,
                            $"Intermediate error parsing SiloInstanceTableEntry to MembershipTableData: {tableEntry}. Ignoring this entry.", exc);
                    }
                }
                var data = new MembershipTableData(memEntries, this.tableVersion);
                return data;
            }
            catch (Exception exc)
            {
                this.logger.Error(ErrorCode.MembershipBase,
                    $"Intermediate error parsing SiloInstanceTableEntry to MembershipTableData: {Utils.EnumerableToString(entries, e => e.ToString())}.", exc);
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

            parse.SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Parse(tableEntry.Address), tableEntry.Port), tableEntry.Generation);

            if (!string.IsNullOrEmpty(tableEntry.SiloName))
            {
                parse.SiloName = tableEntry.SiloName;
            }

            parse.StartTime = !string.IsNullOrEmpty(tableEntry.StartTime) ?
                LogFormatter.ParseDate(tableEntry.StartTime) : default(DateTime);

            parse.IAmAliveTime = !string.IsNullOrEmpty(tableEntry.IAmAliveTime) ?
                LogFormatter.ParseDate(tableEntry.IAmAliveTime) : default(DateTime);

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
                throw new OrleansException(String.Format("SuspectingSilos.Length of {0} as read from Azure table is not eqaul to SuspectingTimes.Length of {1}", suspectingSilos.Count, suspectingTimes.Count));

            for (int i = 0; i < suspectingSilos.Count; i++)
                parse.AddSuspector(suspectingSilos[i], suspectingTimes[i]);

            return parse;
        }

        private SiloInstanceRecord Convert(MembershipEntry memEntry)
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
                SiloIdentity = SiloInstanceRecord.ConstructSiloIdentity(memEntry.SiloAddress)
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
    }
}
