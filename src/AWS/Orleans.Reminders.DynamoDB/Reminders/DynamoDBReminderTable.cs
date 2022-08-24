using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Reminders.DynamoDB
{
    /// <summary>
    /// Implementation for IReminderTable using DynamoDB as underlying storage.
    /// </summary>
    internal sealed class DynamoDBReminderTable : IReminderTable
    {
        private const string GRAIN_REFERENCE_PROPERTY_NAME = "GrainReference";
        private const string REMINDER_NAME_PROPERTY_NAME = "ReminderName";
        private const string SERVICE_ID_PROPERTY_NAME = "ServiceId";
        private const string START_TIME_PROPERTY_NAME = "StartTime";
        private const string PERIOD_PROPERTY_NAME = "Period";
        private const string GRAIN_HASH_PROPERTY_NAME = "GrainHash";
        private const string REMINDER_ID_PROPERTY_NAME = "ReminderId";
        private const string ETAG_PROPERTY_NAME = "ETag";
        private const string CURRENT_ETAG_ALIAS = ":currentETag";
        private const string SERVICE_ID_GRAIN_HASH_INDEX = "ServiceIdIndex";
        private const string SERVICE_ID_GRAIN_REFERENCE_INDEX = "ServiceIdGrainReferenceIndex";

        private readonly ILogger logger;
        private readonly DynamoDBReminderStorageOptions options;
        private readonly string serviceId;

        private DynamoDBStorage storage;

        /// <summary>Initializes a new instance of the <see cref="DynamoDBReminderTable"/> class.</summary>
        /// <param name="loggerFactory">logger factory to use</param>
        /// <param name="clusterOptions"></param>
        /// <param name="storageOptions"></param>
        public DynamoDBReminderTable(
            ILoggerFactory loggerFactory,
            IOptions<ClusterOptions> clusterOptions,
            IOptions<DynamoDBReminderStorageOptions> storageOptions)
        {
            this.logger = loggerFactory.CreateLogger<DynamoDBReminderTable>();
            this.serviceId = clusterOptions.Value.ServiceId;
            this.options = storageOptions.Value;
        }

        /// <summary>Initialize current instance with specific global configuration and logger</summary>
        public Task Init()
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

            this.logger.LogInformation((int)ErrorCode.ReminderServiceBase, "Initializing AWS DynamoDB Reminders Table");

            var serviceIdGrainHashGlobalSecondaryIndex = new GlobalSecondaryIndex
            {
                IndexName = SERVICE_ID_GRAIN_HASH_INDEX,
                Projection = new Projection { ProjectionType = ProjectionType.ALL },
                KeySchema = new List<KeySchemaElement>
                {
                    new KeySchemaElement { AttributeName = SERVICE_ID_PROPERTY_NAME, KeyType = KeyType.HASH},
                    new KeySchemaElement { AttributeName = GRAIN_HASH_PROPERTY_NAME, KeyType = KeyType.RANGE }
                }
            };

            var serviceIdGrainReferenceGlobalSecondaryIndex = new GlobalSecondaryIndex
            {
                IndexName = SERVICE_ID_GRAIN_REFERENCE_INDEX,
                Projection = new Projection { ProjectionType = ProjectionType.ALL },
                KeySchema = new List<KeySchemaElement>
                {
                    new KeySchemaElement { AttributeName = SERVICE_ID_PROPERTY_NAME, KeyType = KeyType.HASH},
                    new KeySchemaElement { AttributeName = GRAIN_REFERENCE_PROPERTY_NAME, KeyType = KeyType.RANGE }
                }
            };

            return this.storage.InitializeTable(this.options.TableName,
                new List<KeySchemaElement>
                {
                    new KeySchemaElement { AttributeName = REMINDER_ID_PROPERTY_NAME, KeyType = KeyType.HASH },
                    new KeySchemaElement { AttributeName = GRAIN_HASH_PROPERTY_NAME, KeyType = KeyType.RANGE }
                },
                new List<AttributeDefinition>
                {
                    new AttributeDefinition { AttributeName = REMINDER_ID_PROPERTY_NAME, AttributeType = ScalarAttributeType.S },
                    new AttributeDefinition { AttributeName = GRAIN_HASH_PROPERTY_NAME, AttributeType = ScalarAttributeType.N },
                    new AttributeDefinition { AttributeName = SERVICE_ID_PROPERTY_NAME, AttributeType = ScalarAttributeType.S },
                    new AttributeDefinition { AttributeName = GRAIN_REFERENCE_PROPERTY_NAME, AttributeType = ScalarAttributeType.S }
                },
                new List<GlobalSecondaryIndex> { serviceIdGrainHashGlobalSecondaryIndex, serviceIdGrainReferenceGlobalSecondaryIndex });
        }

        /// <summary>
        /// Reads a reminder for a grain reference by reminder name.
        /// Read a row from the reminder table
        /// </summary>
        /// <param name="grainId"> grain ref to locate the row </param>
        /// <param name="reminderName"> reminder name to locate the row </param>
        /// <returns> Return the ReminderTableData if the rows were read successfully </returns>
        public async Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName)
        {
            var reminderId = ConstructReminderId(this.serviceId, grainId, reminderName);

            var keys = new Dictionary<string, AttributeValue>
                {
                    { $"{REMINDER_ID_PROPERTY_NAME}", new AttributeValue(reminderId) },
                    { $"{GRAIN_HASH_PROPERTY_NAME}", new AttributeValue { N = grainId.GetUniformHashCode().ToString() } }
                };

            try
            {
                return await this.storage.ReadSingleEntryAsync(this.options.TableName, keys, this.Resolve).ConfigureAwait(false);
            }
            catch (Exception exc)
            {
                this.logger.LogWarning(
                    (int)ErrorCode.ReminderServiceBase,
                    exc,
                    "Intermediate error reading reminder entry {Utils.DictionaryToString(keys)} from table {this.options.TableName}.",
                    Utils.DictionaryToString(keys),
                    this.options.TableName);
                throw;
            }
        }

        /// <summary>
        /// Read one row from the reminder table
        /// </summary>
        /// <param name="grainId">grain ref to locate the row </param>
        /// <returns> Return the ReminderTableData if the rows were read successfully </returns>
        public async Task<ReminderTableData> ReadRows(GrainId grainId)
        {
            var expressionValues = new Dictionary<string, AttributeValue>
                {
                    { $":{SERVICE_ID_PROPERTY_NAME}", new AttributeValue(this.serviceId) },
                    { $":{GRAIN_REFERENCE_PROPERTY_NAME}", new AttributeValue(grainId.ToString()) }
                };

            try
            {
                var expression = $"{SERVICE_ID_PROPERTY_NAME} = :{SERVICE_ID_PROPERTY_NAME} AND {GRAIN_REFERENCE_PROPERTY_NAME} = :{GRAIN_REFERENCE_PROPERTY_NAME}";
                var records = await this.storage.QueryAllAsync(this.options.TableName, expressionValues, expression, this.Resolve, SERVICE_ID_GRAIN_REFERENCE_INDEX, consistentRead: false).ConfigureAwait(false);

                return new ReminderTableData(records);
            }
            catch (Exception exc)
            {
                this.logger.LogWarning(
                    (int)ErrorCode.ReminderServiceBase,
                    exc,
                    "Intermediate error reading reminder entry {Entries} from table {TableName}.",
                    Utils.DictionaryToString(expressionValues),
                    this.options.TableName);
                throw;
            }
        }

        /// <summary>
        /// Reads reminder table data for a given hash range.
        /// </summary>
        /// <param name="begin"></param>
        /// <param name="end"></param>
        /// <returns> Return the RemiderTableData if the rows were read successfully </returns>
        public async Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            Dictionary<string, AttributeValue> expressionValues = null;

            try
            {
                string expression = string.Empty;
                List<ReminderEntry> records;

                if (begin < end)
                {
                    expressionValues = new Dictionary<string, AttributeValue>
                    {
                        { $":{SERVICE_ID_PROPERTY_NAME}", new AttributeValue(this.serviceId) },
                        { $":Begin{GRAIN_HASH_PROPERTY_NAME}", new AttributeValue { N = (begin + 1).ToString() } },
                        { $":End{GRAIN_HASH_PROPERTY_NAME}", new AttributeValue { N = end.ToString() } }
                    };
                    expression = $"{SERVICE_ID_PROPERTY_NAME} = :{SERVICE_ID_PROPERTY_NAME} AND {GRAIN_HASH_PROPERTY_NAME} BETWEEN :Begin{GRAIN_HASH_PROPERTY_NAME} AND :End{GRAIN_HASH_PROPERTY_NAME}";
                    records = await this.storage.QueryAllAsync(this.options.TableName, expressionValues, expression, this.Resolve, SERVICE_ID_GRAIN_HASH_INDEX, consistentRead: false).ConfigureAwait(false);
                }
                else
                {
                    expressionValues = new Dictionary<string, AttributeValue>
                    {
                        { $":{SERVICE_ID_PROPERTY_NAME}", new AttributeValue(this.serviceId) },
                        { $":End{GRAIN_HASH_PROPERTY_NAME}", new AttributeValue { N = end.ToString() } }
                    };
                    expression = $"{SERVICE_ID_PROPERTY_NAME} = :{SERVICE_ID_PROPERTY_NAME} AND {GRAIN_HASH_PROPERTY_NAME} <= :End{GRAIN_HASH_PROPERTY_NAME}";
                    records = await this.storage.QueryAllAsync(this.options.TableName, expressionValues, expression, this.Resolve, SERVICE_ID_GRAIN_HASH_INDEX, consistentRead: false).ConfigureAwait(false);

                    expressionValues = new Dictionary<string, AttributeValue>
                    {
                        { $":{SERVICE_ID_PROPERTY_NAME}", new AttributeValue(this.serviceId) },
                        { $":Begin{GRAIN_HASH_PROPERTY_NAME}", new AttributeValue { N = begin.ToString() } }
                    };
                    expression = $"{SERVICE_ID_PROPERTY_NAME} = :{SERVICE_ID_PROPERTY_NAME} AND {GRAIN_HASH_PROPERTY_NAME} > :Begin{GRAIN_HASH_PROPERTY_NAME}";
                    records.AddRange(await this.storage.QueryAllAsync(this.options.TableName, expressionValues, expression, this.Resolve, SERVICE_ID_GRAIN_HASH_INDEX, consistentRead: false).ConfigureAwait(false));

                }

                return new ReminderTableData(records);
            }
            catch (Exception exc)
            {
                this.logger.LogWarning(
                    (int)ErrorCode.ReminderServiceBase,
                    exc,
                    "Intermediate error reading reminder entry {Utils.DictionaryToString(expressionValues)} from table {this.options.TableName}.",
                    Utils.DictionaryToString(expressionValues),
                    this.options.TableName);
                throw;
            }
        }

        private ReminderEntry Resolve(Dictionary<string, AttributeValue> item)
        {
            return new ReminderEntry
            {
                ETag = item[ETAG_PROPERTY_NAME].N,
                GrainId = GrainId.Parse(item[GRAIN_REFERENCE_PROPERTY_NAME].S),
                Period = TimeSpan.Parse(item[PERIOD_PROPERTY_NAME].S),
                ReminderName = item[REMINDER_NAME_PROPERTY_NAME].S,
                StartAt = DateTime.Parse(item[START_TIME_PROPERTY_NAME].S)
            };
        }

        /// <summary>
        /// Remove one row from the reminder table
        /// </summary>
        /// <param name="grainId"> specific grain ref to locate the row </param>
        /// <param name="reminderName"> reminder name to locate the row </param>
        /// <param name="eTag"> e tag </param>
        /// <returns> Return true if the row was removed </returns>
        public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
        {
            var reminderId = ConstructReminderId(this.serviceId, grainId, reminderName);

            var keys = new Dictionary<string, AttributeValue>
                {
                    { $"{REMINDER_ID_PROPERTY_NAME}", new AttributeValue(reminderId) },
                    { $"{GRAIN_HASH_PROPERTY_NAME}", new AttributeValue { N = grainId.GetUniformHashCode().ToString() } }
                };

            try
            {
                var conditionalValues = new Dictionary<string, AttributeValue> { { CURRENT_ETAG_ALIAS, new AttributeValue { N = eTag } } };
                var expression = $"{ETAG_PROPERTY_NAME} = {CURRENT_ETAG_ALIAS}";

                await this.storage.DeleteEntryAsync(this.options.TableName, keys, expression, conditionalValues).ConfigureAwait(false);
                return true;
            }
            catch (ConditionalCheckFailedException)
            {
                return false;
            }
        }

        /// <summary>
        /// Test hook to clear reminder table data.
        /// </summary>
        /// <returns></returns>
        public async Task TestOnlyClearTable()
        {
            var expressionValues = new Dictionary<string, AttributeValue>
                {
                    { $":{SERVICE_ID_PROPERTY_NAME}", new AttributeValue(this.serviceId) }
                };

            try
            {
                var expression = $"{SERVICE_ID_PROPERTY_NAME} = :{SERVICE_ID_PROPERTY_NAME}";
                var records = await this.storage.ScanAsync(this.options.TableName, expressionValues, expression,
                    item => new Dictionary<string, AttributeValue>
                    {
                        { REMINDER_ID_PROPERTY_NAME, item[REMINDER_ID_PROPERTY_NAME] },
                        { GRAIN_HASH_PROPERTY_NAME, item[GRAIN_HASH_PROPERTY_NAME] }
                    }).ConfigureAwait(false);

                if (records.Count <= 25)
                {
                    await this.storage.DeleteEntriesAsync(this.options.TableName, records);
                }
                else
                {
                    List<Task> tasks = new List<Task>();
                    foreach (var batch in records.BatchIEnumerable(25))
                    {
                        tasks.Add(this.storage.DeleteEntriesAsync(this.options.TableName, batch));
                    }
                    await Task.WhenAll(tasks);
                }
            }
            catch (Exception exc)
            {
                this.logger.LogWarning(
                    (int)ErrorCode.ReminderServiceBase,
                    exc,
                    "Intermediate error removing reminder entries {Entries} from table {TableName}.",
                    Utils.DictionaryToString(expressionValues),
                    this.options.TableName);
                throw;
            }
        }

        /// <summary>
        /// Async method to put an entry into the reminder table
        /// </summary>
        /// <param name="entry"> The entry to put </param>
        /// <returns> Return the entry ETag if entry was upsert successfully </returns>
        public async Task<string> UpsertRow(ReminderEntry entry)
        {
            var reminderId = ConstructReminderId(this.serviceId, entry.GrainId, entry.ReminderName);

            var fields = new Dictionary<string, AttributeValue>
                {
                    { REMINDER_ID_PROPERTY_NAME, new AttributeValue(reminderId) },
                    { GRAIN_HASH_PROPERTY_NAME, new AttributeValue { N = entry.GrainId.GetUniformHashCode().ToString() } },
                    { SERVICE_ID_PROPERTY_NAME, new AttributeValue(this.serviceId) },
                    { GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue( entry.GrainId.ToString()) },
                    { PERIOD_PROPERTY_NAME, new AttributeValue(entry.Period.ToString()) },
                    { START_TIME_PROPERTY_NAME, new AttributeValue(entry.StartAt.ToString()) },
                    { REMINDER_NAME_PROPERTY_NAME, new AttributeValue(entry.ReminderName) },
                    { ETAG_PROPERTY_NAME, new AttributeValue { N = Random.Shared.Next().ToString() } }
                };

            try
            {
                if (this.logger.IsEnabled(LogLevel.Debug)) this.logger.LogDebug("UpsertRow entry = {Entry}, etag = {ETag}", entry.ToString(), entry.ETag);

                await this.storage.PutEntryAsync(this.options.TableName, fields);

                entry.ETag = fields[ETAG_PROPERTY_NAME].N;
                return entry.ETag;
            }
            catch (Exception exc)
            {
                this.logger.LogWarning(
                    (int)ErrorCode.ReminderServiceBase,
                    exc,
                    "Intermediate error updating entry {Entry} to the table {TableName}.",
                    entry.ToString(),
                    options.TableName);
                throw;
            }
        }

        private static string ConstructReminderId(string serviceId, GrainId grainId, string reminderName) => $"{serviceId}_{grainId}_{reminderName}";
    }
}
