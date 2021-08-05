using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Persistence.DynamoDB;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.Storage
{
    /// <summary>
    /// Dynamo DB storage Provider.
    /// Persist Grain State in a DynamoDB table either in Json or Binary format.
    /// </summary>
    public class DynamoDBGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
    {
        private const int MAX_DATA_SIZE = 400 * 1024;
        private const string GRAIN_REFERENCE_PROPERTY_NAME = "GrainReference";
        private const string STRING_STATE_PROPERTY_NAME = "StringState";
        private const string BINARY_STATE_PROPERTY_NAME = "BinaryState";
        private const string GRAIN_TYPE_PROPERTY_NAME = "GrainType";
        private const string ETAG_PROPERTY_NAME = "ETag";
        private const string GRAIN_TTL_PROPERTY_NAME = "GrainTtl";
        private const string CURRENT_ETAG_ALIAS = ":currentETag";

        private readonly DynamoDBStorageOptions options;
        private readonly Serializer serializer;
        private readonly ILogger logger;
        private readonly IServiceProvider serviceProvider;
        private readonly GrainReferenceKeyStringConverter grainReferenceConverter;
        private readonly string name;

        private DynamoDBStorage storage;
        private JsonSerializerSettings jsonSettings;

        /// <summary>
        /// Default Constructor
        /// </summary>
        public DynamoDBGrainStorage(
            string name,
            DynamoDBStorageOptions options,
            Serializer serializer,
            IServiceProvider serviceProvider,
            GrainReferenceKeyStringConverter grainReferenceConverter,
            ILogger<DynamoDBGrainStorage> logger)
        {
            this.name = name;
            this.logger = logger;
            this.options = options;
            this.serializer = serializer;
            this.serviceProvider = serviceProvider;
            this.grainReferenceConverter = grainReferenceConverter;
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(OptionFormattingUtilities.Name<DynamoDBGrainStorage>(this.name), this.options.InitStage, Init, Close);
        }

        /// <summary> Initialization function for this storage provider. </summary>
        public async Task Init(CancellationToken ct)
        {
            var stopWatch = Stopwatch.StartNew();

            try
            {
                var initMsg = string.Format("Init: Name={0} ServiceId={1} Table={2} DeleteStateOnClear={3}",
                        this.name, this.options.ServiceId, this.options.TableName, this.options.DeleteStateOnClear);

                this.jsonSettings = OrleansJsonSerializer.UpdateSerializerSettings(
                    OrleansJsonSerializer.GetDefaultSerializerSettings(this.serviceProvider),
                    this.options.UseFullAssemblyNames, this.options.IndentJson, this.options.TypeNameHandling);
                this.options.ConfigureJsonSerializerSettings?.Invoke(this.jsonSettings);

                this.logger.LogInformation((int)ErrorCode.StorageProviderBase, $"AWS DynamoDB Grain Storage {this.name} is initializing: {initMsg}");

                this.storage = new DynamoDBStorage(this.logger, this.options.Service, this.options.AccessKey, this.options.SecretKey,
                 this.options.Token, this.options.ProfileName, this.options.ReadCapacityUnits, this.options.WriteCapacityUnits, this.options.UseProvisionedThroughput);

                await storage.InitializeTable(this.options.TableName,
                    new List<KeySchemaElement>
                    {
                        new KeySchemaElement { AttributeName = GRAIN_REFERENCE_PROPERTY_NAME, KeyType = KeyType.HASH },
                        new KeySchemaElement { AttributeName = GRAIN_TYPE_PROPERTY_NAME, KeyType = KeyType.RANGE }
                    },
                    new List<AttributeDefinition>
                    {
                        new AttributeDefinition { AttributeName = GRAIN_REFERENCE_PROPERTY_NAME, AttributeType = ScalarAttributeType.S },
                        new AttributeDefinition { AttributeName = GRAIN_TYPE_PROPERTY_NAME, AttributeType = ScalarAttributeType.S }
                    },
                    secondaryIndexes: null,
                    ttlAttributeName: this.options.TimeToLive.HasValue ? GRAIN_TTL_PROPERTY_NAME : null);
                stopWatch.Stop();
                this.logger.LogInformation((int)ErrorCode.StorageProviderBase,
                    $"Initializing provider {this.name} of type {this.GetType().Name} in stage {this.options.InitStage} took {stopWatch.ElapsedMilliseconds} Milliseconds.");
            }
            catch (Exception exc)
            {
                stopWatch.Stop();
                this.logger.LogError((int)ErrorCode.Provider_ErrorFromInit, $"Initialization failed for provider {this.name} of type {this.GetType().Name} in stage {this.options.InitStage} in {stopWatch.ElapsedMilliseconds} Milliseconds.", exc);
                throw;
            }
        }

        /// <summary> Shutdown this storage provider. </summary>
        public Task Close(CancellationToken ct) => Task.CompletedTask;

        /// <summary> Read state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.ReadStateAsync"/>
        public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            if (this.storage == null) throw new ArgumentException("GrainState-Table property not initialized");

            string partitionKey = GetKeyString(grainReference);
            if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace(ErrorCode.StorageProviderBase,
                "Reading: GrainType={0} Pk={1} Grainid={2} from Table={3}",
                grainType, partitionKey, grainReference, this.options.TableName);

            string rowKey = AWSUtils.ValidateDynamoDBRowKey(grainType);

            var record = await this.storage.ReadSingleEntryAsync(this.options.TableName,
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
                var loadedState = ConvertFromStorageFormat(record, grainState.Type);
                grainState.RecordExists = loadedState != null;
                grainState.State = loadedState ?? Activator.CreateInstance(grainState.Type);
                grainState.ETag = record.ETag.ToString();
            }

            // Else leave grainState in previous default condition
        }

        /// <summary> Write state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.WriteStateAsync"/>
        public async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            if (this.storage == null) throw new ArgumentException("GrainState-Table property not initialized");

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
                this.logger.Error(ErrorCode.StorageProviderBase,
                    string.Format("Error Writing: GrainType={0} Grainid={1} ETag={2} to Table={3} Exception={4}",
                    grainType, grainReference, grainState.ETag, this.options.TableName, exc.Message), exc);
                throw;
            }
        }

        private async Task WriteStateInternal(IGrainState grainState, GrainStateRecord record, bool clear = false)
        {
            var fields = new Dictionary<string, AttributeValue>();
            if (this.options.TimeToLive.HasValue)
            {
                fields.Add(GRAIN_TTL_PROPERTY_NAME, new AttributeValue { N = ((DateTimeOffset)DateTime.UtcNow.Add(this.options.TimeToLive.Value)).ToUnixTimeSeconds().ToString() });
            }

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

                int currentEtag;
                int.TryParse(grainState.ETag, out currentEtag);
                newEtag = currentEtag;
                fields.Add(ETAG_PROPERTY_NAME, new AttributeValue { N = newEtag++.ToString() });

                await this.storage.PutEntryAsync(this.options.TableName, fields).ConfigureAwait(false);
            }
            else if (string.IsNullOrWhiteSpace(grainState.ETag))
            {
                fields.Add(GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(record.GrainReference));
                fields.Add(GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(record.GrainType));
                fields.Add(ETAG_PROPERTY_NAME, new AttributeValue { N = "0" });

                var expression = $"attribute_not_exists({GRAIN_REFERENCE_PROPERTY_NAME}) AND attribute_not_exists({GRAIN_TYPE_PROPERTY_NAME})";
                await this.storage.PutEntryAsync(this.options.TableName, fields, expression).ConfigureAwait(false);
            }
            else
            {
                var keys = new Dictionary<string, AttributeValue>();
                keys.Add(GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(record.GrainReference));
                keys.Add(GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(record.GrainType));

                int currentEtag;
                int.TryParse(grainState.ETag, out currentEtag);
                newEtag = currentEtag;
                newEtag++;
                fields.Add(ETAG_PROPERTY_NAME, new AttributeValue { N = newEtag.ToString() });

                var conditionalValues = new Dictionary<string, AttributeValue> { { CURRENT_ETAG_ALIAS, new AttributeValue { N = currentEtag.ToString() } } };
                var expression = $"{ETAG_PROPERTY_NAME} = {CURRENT_ETAG_ALIAS}";
                await this.storage.UpsertEntryAsync(this.options.TableName, keys, fields, expression, conditionalValues).ConfigureAwait(false);
            }

            grainState.ETag = newEtag.ToString();
            grainState.RecordExists = !clear;
        }

        /// <summary> Clear / Delete state data function for this storage provider. </summary>
        /// <remarks>
        /// If the <c>DeleteStateOnClear</c> is set to <c>true</c> then the table row
        /// for this grain will be deleted / removed, otherwise the table row will be
        /// cleared by overwriting with default / null values.
        /// </remarks>
        /// <see cref="IGrainStorage.ClearStateAsync"/>
        public async Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            if (this.storage == null) throw new ArgumentException("GrainState-Table property not initialized");

            string partitionKey = GetKeyString(grainReference);
            if (this.logger.IsEnabled(LogLevel.Trace))
            {
                this.logger.Trace(ErrorCode.StorageProviderBase,
                    "Clearing: GrainType={0} Pk={1} Grainid={2} ETag={3} DeleteStateOnClear={4} from Table={5}",
                    grainType, partitionKey, grainReference, grainState.ETag, this.options.DeleteStateOnClear, this.options.TableName);
            }
            string rowKey = AWSUtils.ValidateDynamoDBRowKey(grainType);
            var record = new GrainStateRecord { GrainReference = partitionKey, ETag = string.IsNullOrWhiteSpace(grainState.ETag) ? 0 : int.Parse(grainState.ETag), GrainType = rowKey };

            var operation = "Clearing";
            try
            {
                if (this.options.DeleteStateOnClear)
                {
                    operation = "Deleting";
                    var keys = new Dictionary<string, AttributeValue>();
                    keys.Add(GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(record.GrainReference));
                    keys.Add(GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(record.GrainType));

                    await this.storage.DeleteEntryAsync(this.options.TableName, keys).ConfigureAwait(false);
                    grainState.ETag = string.Empty;
                }
                else
                {
                    await WriteStateInternal(grainState, record, true);
                }
            }
            catch (Exception exc)
            {
                this.logger.Error(ErrorCode.StorageProviderBase, string.Format("Error {0}: GrainType={1} Grainid={2} ETag={3} from Table={4} Exception={5}",
                    operation, grainType, grainReference, grainState.ETag, this.options.TableName, exc.Message), exc);
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
            var key = string.Format("{0}_{1}", this.options.ServiceId, this.grainReferenceConverter.ToKeyString(grainReference));
            return AWSUtils.ValidateDynamoDBPartitionKey(key);
        }

        internal object ConvertFromStorageFormat(GrainStateRecord entity, Type stateType)
        {
            var binaryData = entity.BinaryState;
            var stringData = entity.StringState;

            object dataValue = null;
            try
            {
                if (binaryData?.Length > 0)
                {
                    // Rehydrate
                    dataValue = this.serializer.Deserialize<object>(binaryData);
                }
                else if (!string.IsNullOrEmpty(stringData))
                {
                    dataValue = JsonConvert.DeserializeObject(stringData, stateType, this.jsonSettings);
                }

                // Else, no data found
            }
            catch (Exception exc)
            {
                var sb = new StringBuilder();
                if (binaryData?.Length > 0)
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

                this.logger.Error(0, sb.ToString(), exc);
                throw new AggregateException(sb.ToString(), exc);
            }

            return dataValue;
        }

        internal void ConvertToStorageFormat(object grainState, GrainStateRecord entity)
        {
            int dataSize;
            if (this.options.UseJson)
            {
                // http://james.newtonking.com/json/help/index.html?topic=html/T_Newtonsoft_Json_JsonConvert.htm
                entity.StringState = JsonConvert.SerializeObject(grainState, this.jsonSettings);
                dataSize = STRING_STATE_PROPERTY_NAME.Length + entity.StringState.Length;

                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace("Writing JSON data size = {0} for grain id = Partition={1} / Row={2}",
                    dataSize, entity.GrainReference, entity.GrainType);
            }
            else
            {
                // Convert to binary format
                entity.BinaryState = this.serializer.SerializeToArray(grainState);
                dataSize = BINARY_STATE_PROPERTY_NAME.Length + entity.BinaryState.Length;

                if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace("Writing binary data size = {0} for grain id = Partition={1} / Row={2}",
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

    public static class DynamoDBGrainStorageFactory
    {
        public static IGrainStorage Create(IServiceProvider services, string name)
        {
            var optionsMonitor = services.GetRequiredService<IOptionsMonitor<DynamoDBStorageOptions>>();
            return ActivatorUtilities.CreateInstance<DynamoDBGrainStorage>(services, optionsMonitor.Get(name), name);
        }
    }
}
