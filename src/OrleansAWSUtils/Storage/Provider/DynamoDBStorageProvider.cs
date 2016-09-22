using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime;
using Newtonsoft.Json;
using System.Threading;
using Orleans.Serialization;
using Amazon.DynamoDBv2.Model;
using Amazon.DynamoDBv2;
using OrleansAWSUtils;
using System.IO;
using OrleansAWSUtils.Storage;

namespace Orleans.Storage
{
    /// <summary>
    /// Dynamo DB storage Provider
    /// Persist Grain State in a DynamoDB table either in Json or Binary format
    /// </summary>
    ///  /// <para>
    /// Required configuration params: <c>DataConnectionString</c>
    /// </para>
    /// <para>
    /// Optional configuration params:
    /// <c>TableName</c> -- defaults to <c>OrleansGrainState</c>
    /// <c>DeleteStateOnClear</c> -- defaults to <c>false</c>
    /// </para>
    public class DynamoDBStorageProvider : IStorageProvider
    {
        private const int MAX_DATA_SIZE = 400 * 1024;
        private const string TABLE_NAME_DEFAULT_VALUE = "OrleansGrainState";
        private const string DELETE_ON_CLEAR_PROPERTY_NAME = "DeleteStateOnClear";
        private const string TABLE_NAME_PROPERTY_NAME = "TableName";
        private const string USE_JSON_FORMAT_PROPERTY_NAME = "UseJsonFormat";
        private const string DATA_CONNECTION_STRING_PROPERTY_NAME = "DataConnectionString";
        private const string GRAIN_REFERENCE_PROPERTY_NAME = "GrainReference";
        private const string STRING_STATE_PROPERTY_NAME = "StringState";
        private const string BINARY_STATE_PROPERTY_NAME = "BinaryState";
        private const string GRAIN_TYPE_PROPERTY_NAME = "GrainType";
        private const string ETAG_PROPERTY_NAME = "ETag";
        private const string CURRENT_ETAG_ALIAS = ":currentETag";
        private string tableName;
        private static int counter;
        private readonly int id;
        private string serviceId;
        private bool isDeleteStateOnClear = false;
        private bool useJsonFormat;
        private JsonSerializerSettings jsonSettings;

        /// <summary>
        /// Provider Name
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// Orleans Logger instance
        /// </summary>
        public Logger Log { get; private set; }

        private DynamoDBStorage storage;

        /// <summary>
        /// Default Constructor
        /// </summary>
        public DynamoDBStorageProvider()
        {
            tableName = TABLE_NAME_DEFAULT_VALUE;
            id = Interlocked.Increment(ref counter);
        }

        /// <summary> Initialization function for this storage provider. </summary>
        /// <see cref="IProvider.Init"/>
        public Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            Name = name;
            serviceId = providerRuntime.ServiceId.ToString();

            if (config.Properties.ContainsKey(TABLE_NAME_PROPERTY_NAME))
                tableName = config.Properties[TABLE_NAME_PROPERTY_NAME];

            isDeleteStateOnClear = config.Properties.ContainsKey(DELETE_ON_CLEAR_PROPERTY_NAME) &&
                "true".Equals(config.Properties[DELETE_ON_CLEAR_PROPERTY_NAME], StringComparison.OrdinalIgnoreCase);

            Log = providerRuntime.GetLogger("Storage.AWSDynamoDBStorage." + id);

            var initMsg = string.Format("Init: Name={0} ServiceId={1} Table={2} DeleteStateOnClear={3}",
                Name, serviceId, tableName, isDeleteStateOnClear);

            if (config.Properties.ContainsKey(USE_JSON_FORMAT_PROPERTY_NAME))
                useJsonFormat = "true".Equals(config.Properties[USE_JSON_FORMAT_PROPERTY_NAME], StringComparison.OrdinalIgnoreCase);

            this.jsonSettings = OrleansJsonSerializer.UpdateSerializerSettings(OrleansJsonSerializer.GetDefaultSerializerSettings(), config);

            initMsg = string.Format("{0} UseJsonFormat={1}", initMsg, useJsonFormat);

            Log.Info(ErrorCode.StorageProviderBase, "AWS DynamoDB Provider: {0}", initMsg);

            storage = new DynamoDBStorage(config.Properties[DATA_CONNECTION_STRING_PROPERTY_NAME], Log);
            return storage.InitializeTable(tableName,
                new List<KeySchemaElement>
                {
                    new KeySchemaElement { AttributeName = GRAIN_REFERENCE_PROPERTY_NAME, KeyType = KeyType.HASH },
                    new KeySchemaElement { AttributeName = GRAIN_TYPE_PROPERTY_NAME, KeyType = KeyType.RANGE }
                },
                new List<AttributeDefinition>
                {
                    new AttributeDefinition { AttributeName = GRAIN_REFERENCE_PROPERTY_NAME, AttributeType = ScalarAttributeType.S },
                    new AttributeDefinition { AttributeName = GRAIN_TYPE_PROPERTY_NAME, AttributeType = ScalarAttributeType.S }
                });
        }

        internal void InitLogger(Logger logger)
        {
            Log = logger;
        }

        /// <summary> Shutdown this storage provider. </summary>
        /// <see cref="IProvider.Close"/>
        public Task Close()
        {
            return TaskDone.Done;
        }

        /// <summary> Read state data function for this storage provider. </summary>
        /// <see cref="IStorageProvider.ReadStateAsync"/>
        public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            if (storage == null) throw new ArgumentException("GrainState-Table property not initialized");

            string partitionKey = GetKeyString(grainReference);
            if (Log.IsVerbose3) Log.Verbose3(ErrorCode.StorageProviderBase, "Reading: GrainType={0} Pk={1} Grainid={2} from Table={3}", grainType, partitionKey, grainReference, tableName);
            string rowKey = AWSUtils.ValidateDynamoDBRowKey(grainType);

            var record = await storage.ReadSingleEntryAsync(tableName,
                new Dictionary<string, AttributeValue>
                {
                    { GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(partitionKey) },
                    { GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(rowKey) }
                },
                (fields) =>
                {
                    return new GrainStateRecord
                    {
                        GrainType = fields[GRAIN_TYPE_PROPERTY_NAME].S,
                        GrainReference = fields[GRAIN_REFERENCE_PROPERTY_NAME].S,
                        ETag = int.Parse(fields[ETAG_PROPERTY_NAME].N),
                        BinaryState = fields.ContainsKey(BINARY_STATE_PROPERTY_NAME) ? fields[BINARY_STATE_PROPERTY_NAME].B.ToArray() : null,
                        StringState = fields.ContainsKey(STRING_STATE_PROPERTY_NAME) ? fields[STRING_STATE_PROPERTY_NAME].S : string.Empty
                    };
                }).ConfigureAwait(false);

            if (record != null)
            {
                var loadedState = ConvertFromStorageFormat(record);
                grainState.State = loadedState ?? Activator.CreateInstance(grainState.State.GetType());
                grainState.ETag = record.ETag.ToString();
            }

            // Else leave grainState in previous default condition
        }

        /// <summary> Write state data function for this storage provider. </summary>
        /// <see cref="IStorageProvider.WriteStateAsync"/>
        public async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            if (storage == null) throw new ArgumentException("GrainState-Table property not initialized");

            string partitionKey = GetKeyString(grainReference);
            string rowKey = AWSUtils.ValidateDynamoDBRowKey(grainType);

            var record = new GrainStateRecord { GrainReference = partitionKey, GrainType = rowKey };

            try
            {
                ConvertToStorageFormat(grainState.State, record);
                await WriteStateInternal(grainState, record);
            }
            catch (ConditionalCheckFailedException exc)
            {
                throw new InconsistentStateException("Invalid grain state", exc);
            }
            catch (Exception exc)
            {
                Log.Error(ErrorCode.StorageProviderBase, string.Format("Error Writing: GrainType={0} Grainid={1} ETag={2} to Table={3} Exception={4}",
                    grainType, grainReference, grainState.ETag, tableName, exc.Message), exc);
                throw;
            }
        }

        private async Task WriteStateInternal(IGrainState grainState, GrainStateRecord record, bool clear = false)
        {
            var fields = new Dictionary<string, AttributeValue>();

            if (record.BinaryState != null && record.BinaryState.Length > 0)
            {
                fields.Add(BINARY_STATE_PROPERTY_NAME, new AttributeValue { B = new MemoryStream(record.BinaryState) });
            }
            else if (!string.IsNullOrWhiteSpace(record.StringState))
            {
                fields.Add(STRING_STATE_PROPERTY_NAME, new AttributeValue(record.StringState));
            }

            int newEtag = 0;
            if (clear)
            {
                fields.Add(GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(record.GrainReference));
                fields.Add(GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(record.GrainType));

                int currentEtag = 0;
                int.TryParse(grainState.ETag, out currentEtag);
                newEtag = currentEtag;
                fields.Add(ETAG_PROPERTY_NAME, new AttributeValue { N = newEtag++.ToString() });

                await storage.PutEntryAsync(tableName, fields).ConfigureAwait(false);
            }
            else if (string.IsNullOrWhiteSpace(grainState.ETag))
            {
                fields.Add(GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(record.GrainReference));
                fields.Add(GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(record.GrainType));
                fields.Add(ETAG_PROPERTY_NAME, new AttributeValue { N = "0" });

                var expression = $"attribute_not_exists({GRAIN_REFERENCE_PROPERTY_NAME}) AND attribute_not_exists({GRAIN_TYPE_PROPERTY_NAME})";
                await storage.PutEntryAsync(tableName, fields, expression).ConfigureAwait(false);
            }
            else
            {
                var keys = new Dictionary<string, AttributeValue>();
                keys.Add(GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(record.GrainReference));
                keys.Add(GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(record.GrainType));

                int currentEtag = 0;
                int.TryParse(grainState.ETag, out currentEtag);
                newEtag = currentEtag;
                newEtag++;
                fields.Add(ETAG_PROPERTY_NAME, new AttributeValue { N = newEtag.ToString() });

                var conditionalValues = new Dictionary<string, AttributeValue> { { CURRENT_ETAG_ALIAS, new AttributeValue { N = currentEtag.ToString() } } };
                var expression = $"{ETAG_PROPERTY_NAME} = {CURRENT_ETAG_ALIAS}";
                await storage.UpsertEntryAsync(tableName, keys, fields, expression, conditionalValues).ConfigureAwait(false);
            }

            grainState.ETag = newEtag.ToString();
        }

        /// <summary> Clear / Delete state data function for this storage provider. </summary>
        /// <remarks>
        /// If the <c>DeleteStateOnClear</c> is set to <c>true</c> then the table row
        /// for this grain will be deleted / removed, otherwise the table row will be
        /// cleared by overwriting with default / null values.
        /// </remarks>
        /// <see cref="IStorageProvider.ClearStateAsync"/>
        public async Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            if (storage == null) throw new ArgumentException("GrainState-Table property not initialized");

            string partitionKey = GetKeyString(grainReference);
            if (Log.IsVerbose3) Log.Verbose3(ErrorCode.StorageProviderBase, "Clearing: GrainType={0} Pk={1} Grainid={2} ETag={3} DeleteStateOnClear={4} from Table={5}", grainType, partitionKey, grainReference, grainState.ETag, isDeleteStateOnClear, tableName);
            string rowKey = AWSUtils.ValidateDynamoDBRowKey(grainType);
            var record = new GrainStateRecord { GrainReference = partitionKey, ETag = string.IsNullOrWhiteSpace(grainState.ETag) ? 0 : int.Parse(grainState.ETag), GrainType = rowKey };

            var operation = "Clearing";
            try
            {
                if (isDeleteStateOnClear)
                {
                    operation = "Deleting";
                    var keys = new Dictionary<string, AttributeValue>();
                    keys.Add(GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(record.GrainReference));
                    keys.Add(GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(record.GrainType));

                    await storage.DeleteEntryAsync(tableName, keys).ConfigureAwait(false);
                    grainState.ETag = string.Empty;
                }
                else
                {
                    await WriteStateInternal(grainState, record, true);
                }
            }
            catch (Exception exc)
            {
                Log.Error(ErrorCode.StorageProviderBase, string.Format("Error {0}: GrainType={1} Grainid={2} ETag={3} from Table={4} Exception={5}",
                    operation, grainType, grainReference, grainState.ETag, tableName, exc.Message), exc);
                throw;
            }
        }

        internal class GrainStateRecord
        {
            public string GrainReference { get; set; } = "";
            public string GrainType { get; set; } = "";
            public byte[] BinaryState { get; set; }
            public string StringState { get; set; }
            public int ETag { get; set; }
        }

        private string GetKeyString(GrainReference grainReference)
        {
            var key = string.Format("{0}_{1}", serviceId, grainReference.ToKeyString());
            return AWSUtils.ValidateDynamoDBPartitionKey(key);
        }

        internal object ConvertFromStorageFormat(GrainStateRecord entity)
        {
            var binaryData = entity.BinaryState;
            var stringData = entity.StringState;

            object dataValue = null;
            try
            {
                if (binaryData?.Length > 0)
                {
                    // Rehydrate
                    dataValue = SerializationManager.DeserializeFromByteArray<object>(binaryData);
                }
                else if (!string.IsNullOrEmpty(stringData))
                {
                    dataValue = JsonConvert.DeserializeObject<object>(stringData, jsonSettings);
                }

                // Else, no data found
            }
            catch (Exception exc)
            {
                var sb = new StringBuilder();
                if (binaryData.Length > 0)
                {
                    sb.AppendFormat("Unable to convert from storage format GrainStateEntity.Data={0}", binaryData);
                }
                else if (!string.IsNullOrEmpty(stringData))
                {
                    sb.AppendFormat("Unable to convert from storage format GrainStateEntity.StringData={0}", stringData);
                }
                if (dataValue != null)
                {
                    sb.AppendFormat("Data Value={0} Type={1}", dataValue, dataValue.GetType());
                }

                Log.Error(0, sb.ToString(), exc);
                throw new AggregateException(sb.ToString(), exc);
            }

            return dataValue;
        }

        internal void ConvertToStorageFormat(object grainState, GrainStateRecord entity)
        {
            int dataSize;
            if (useJsonFormat)
            {
                // http://james.newtonking.com/json/help/index.html?topic=html/T_Newtonsoft_Json_JsonConvert.htm
                entity.StringState = JsonConvert.SerializeObject(grainState, jsonSettings);
                dataSize = STRING_STATE_PROPERTY_NAME.Length + entity.StringState.Length;

                if (Log.IsVerbose3) Log.Verbose3("Writing JSON data size = {0} for grain id = Partition={1} / Row={2}",
                    dataSize, entity.GrainReference, entity.GrainType);
            }
            else
            {
                // Convert to binary format
                entity.BinaryState = SerializationManager.SerializeToByteArray(grainState);
                dataSize = BINARY_STATE_PROPERTY_NAME.Length + entity.BinaryState.Length;

                if (Log.IsVerbose3) Log.Verbose3("Writing binary data size = {0} for grain id = Partition={1} / Row={2}",
                    dataSize, entity.GrainReference, entity.GrainType);
            }

            var pkSize = GRAIN_REFERENCE_PROPERTY_NAME.Length + entity.GrainReference.Length;
            var rkSize = GRAIN_TYPE_PROPERTY_NAME.Length + entity.GrainType.Length;
            var versionSize = ETAG_PROPERTY_NAME.Length + entity.ETag.ToString().Length;

            if ((pkSize + rkSize + versionSize + dataSize) > MAX_DATA_SIZE)
            {
                var msg = string.Format("Data too large to write to DynamoDB table. Size={0} MaxSize={1}", dataSize, MAX_DATA_SIZE);
                throw new ArgumentOutOfRangeException("GrainState.Size", msg);
            }
        }
    }
}
