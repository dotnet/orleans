using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Orleans.Runtime.Configuration;
using OrleansAWSUtils.Storage;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Runtime.MembershipService
{
    internal class DynamoDBMembershipTable : IMembershipTable
    {
        //DynamoDB does not support the extended Membership Protocol and will always return the same table version information
        private readonly TableVersion _tableVersion = new TableVersion(0, "0");

        private const string TABLE_NAME_DEFAULT_VALUE = "OrleansSiloInstances";
        private const string CURRENT_ETAG_ALIAS = ":currentETag";
        private const int MAX_BATCH_SIZE = 25;

        private Logger logger;
        private DynamoDBStorage storage;
        private string deploymentId;

        public Task InitializeMembershipTable(GlobalConfiguration config, bool tryInitTableVersion, Logger log)
        {
            logger = log;
            deploymentId = config.DeploymentId;
            storage = new DynamoDBStorage(config.DataConnectionString, log);
            logger.Info(ErrorCode.MembershipBase, "Initializing AWS DynamoDB Membership Table");
            return storage.InitializeTable(TABLE_NAME_DEFAULT_VALUE,
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

        public async Task DeleteMembershipTableEntries(string deploymentId)
        {
            try
            {
                var keys = new Dictionary<string, AttributeValue> { { $":{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", new AttributeValue(deploymentId) } };
                var records = await storage.QueryAsync(TABLE_NAME_DEFAULT_VALUE, keys, $"{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME} = :{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", item => new SiloInstanceRecord(item));

                var toDelete = new List<Dictionary<string, AttributeValue>>();
                foreach (var record in records)
                {
                    toDelete.Add(record.GetKeys());
                }

                List<Task> tasks = new List<Task>();
                foreach (var batch in toDelete.BatchIEnumerable(MAX_BATCH_SIZE))
                {
                    tasks.Add(storage.DeleteEntriesAsync(TABLE_NAME_DEFAULT_VALUE, batch));
                }
                await Task.WhenAll(tasks);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.MembershipBase, string.Format("Unable to delete membership records on table {0} for deploymentId {1}: Exception={2}",
                    TABLE_NAME_DEFAULT_VALUE, deploymentId, exc));
                throw;
            }
        }

        public async Task<MembershipTableData> ReadRow(SiloAddress siloAddress)
        {
            try
            {
                var keys = new Dictionary<string, AttributeValue>
                {
                    { $"{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", new AttributeValue(deploymentId) },
                    { $"{SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME}", new AttributeValue(SiloInstanceRecord.ConstructSiloIdentity(siloAddress)) }
                };
                var entry = await storage.ReadSingleEntryAsync(TABLE_NAME_DEFAULT_VALUE, keys, fields => new SiloInstanceRecord(fields));
                MembershipTableData data = entry != null ? Convert(new List<SiloInstanceRecord> { entry }) : new MembershipTableData(_tableVersion);
                if (logger.IsVerbose2) logger.Verbose2("Read my entry {0} Table=" + Environment.NewLine + "{1}", siloAddress.ToLongString(), data.ToString());
                return data;
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.MembershipBase,
                    $"Intermediate error reading silo entry for key {siloAddress.ToLongString()} from the table {TABLE_NAME_DEFAULT_VALUE}.", exc);
                throw;
            }
        }

        public async Task<MembershipTableData> ReadAll()
        {
            try
            {
                var keys = new Dictionary<string, AttributeValue> { { $":{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", new AttributeValue(deploymentId) } };
                var records = await storage.QueryAsync(TABLE_NAME_DEFAULT_VALUE, keys, $"{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME} = :{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", item => new SiloInstanceRecord(item));

                MembershipTableData data = Convert(records);
                if (logger.IsVerbose2) logger.Verbose2("ReadAll Table=" + Environment.NewLine + "{0}", data.ToString());

                return data;
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.MembershipBase,
                    $"Intermediate error reading all silo entries {TABLE_NAME_DEFAULT_VALUE}.", exc);
                throw;
            }
        }

        public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            try
            {
                if (logger.IsVerbose) logger.Verbose("InsertRow entry = {0}", entry.ToFullString());
                var tableEntry = Convert(entry);

                bool result;

                try
                {
                    var expression = $"attribute_not_exists({SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}) AND attribute_not_exists({SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME})";

                    await storage.PutEntryAsync(TABLE_NAME_DEFAULT_VALUE, tableEntry.GetFields(true), expression);
                    result = true;
                }
                catch (ConditionalCheckFailedException)
                {
                    result = false;
                    logger.Warn(ErrorCode.MembershipBase,
                        $"Insert failed due to contention on the table. Will retry. Entry {entry.ToFullString()}");
                }
                    
                return result;
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.MembershipBase,
                    $"Intermediate error inserting entry {entry.ToFullString()} to the table {TABLE_NAME_DEFAULT_VALUE}.", exc);
                throw;
            }
        }

        public async Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            try
            {
                if (logger.IsVerbose) logger.Verbose("UpdateRow entry = {0}, etag = {1}", entry.ToFullString(), etag);
                var siloEntry = Convert(entry);
                int currentEtag = 0;
                if (!int.TryParse(etag, out currentEtag))
                {
                    logger.Warn(ErrorCode.MembershipBase,
                        $"Update failed. Invalid ETag value. Will retry. Entry {entry.ToFullString()}, eTag {etag}");
                    return false;
                }

                siloEntry.ETag = currentEtag + 1;

                bool result;

                try
                {
                    var conditionalValues = new Dictionary<string, AttributeValue> { { CURRENT_ETAG_ALIAS, new AttributeValue { N = etag } } };
                    var etagConditionalExpression = $"{SiloInstanceRecord.ETAG_PROPERTY_NAME} = {CURRENT_ETAG_ALIAS}";
                    await storage.UpsertEntryAsync(TABLE_NAME_DEFAULT_VALUE, siloEntry.GetKeys(),
                        siloEntry.GetFields(), etagConditionalExpression, conditionalValues);

                    result = true;
                }
                catch (ConditionalCheckFailedException)
                {
                    result = false;
                    logger.Warn(ErrorCode.MembershipBase,
                        $"Update failed due to contention on the table. Will retry. Entry {entry.ToFullString()}, eTag {etag}");
                }
                    
                return result;
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.MembershipBase,
                    $"Intermediate error updating entry {entry.ToFullString()} to the table {TABLE_NAME_DEFAULT_VALUE}.", exc);
                throw;
            }
        }

        public async Task UpdateIAmAlive(MembershipEntry entry)
        {
            try
            {
                if (logger.IsVerbose) logger.Verbose("Merge entry = {0}", entry.ToFullString());
                var siloEntry = ConvertPartial(entry);
                var fields = new Dictionary<string, AttributeValue> { { SiloInstanceRecord.I_AM_ALIVE_TIME_PROPERTY_NAME, new AttributeValue(siloEntry.IAmAliveTime) } };
                var expression = $"attribute_exists({SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}) AND attribute_exists({SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME})";
                await storage.UpsertEntryAsync(TABLE_NAME_DEFAULT_VALUE, siloEntry.GetKeys(),fields, expression);
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.MembershipBase,
                    $"Intermediate error updating IAmAlive field for entry {entry.ToFullString()} to the table {TABLE_NAME_DEFAULT_VALUE}.", exc);
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
                        logger.Error(ErrorCode.MembershipBase,
                            $"Intermediate error parsing SiloInstanceTableEntry to MembershipTableData: {tableEntry}. Ignoring this entry.", exc);
                    }
                }
                var data = new MembershipTableData(memEntries, _tableVersion);
                return data;
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.AzureTable_60,
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
                DeploymentId = deploymentId,
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
                DeploymentId = deploymentId,
                IAmAliveTime = LogFormatter.PrintDate(memEntry.IAmAliveTime),
                SiloIdentity = SiloInstanceRecord.ConstructSiloIdentity(memEntry.SiloAddress)
            };
        }
    }
}
