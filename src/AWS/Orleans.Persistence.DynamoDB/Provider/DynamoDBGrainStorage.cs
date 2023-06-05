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
using Orleans.Configuration;
using Orleans.Persistence.DynamoDB;
using Orleans.Runtime;

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
        private const string BINARY_STATE_PROPERTY_NAME = "GrainState";
        private const string GRAIN_TYPE_PROPERTY_NAME = "GrainType";
        private const string ETAG_PROPERTY_NAME = "ETag";
        private const string GRAIN_TTL_PROPERTY_NAME = "GrainTtl";
        private const string CURRENT_ETAG_ALIAS = ":currentETag";

        private readonly DynamoDBStorageOptions options;
        private readonly ILogger logger;
        private readonly IServiceProvider serviceProvider;
        private readonly string name;

        private DynamoDBStorage storage;

        /// <summary>
        /// Default Constructor
        /// </summary>
        public DynamoDBGrainStorage(
            string name,
            DynamoDBStorageOptions options,
            IServiceProvider serviceProvider,
            ILogger<DynamoDBGrainStorage> logger)
        {
            this.name = name;
            this.logger = logger;
            this.options = options;
            this.serviceProvider = serviceProvider;
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(OptionFormattingUtilities.Name<DynamoDBGrainStorage>(name), options.InitStage, Init, Close);
        }

        /// <summary> Initialization function for this storage provider. </summary>
        public async Task Init(CancellationToken ct)
        {
            var stopWatch = Stopwatch.StartNew();

            try
            {
                var initMsg = string.Format("Init: Name={0} ServiceId={1} Table={2} DeleteStateOnClear={3}",
                        name, options.ServiceId, options.TableName, options.DeleteStateOnClear);

                logger.LogInformation((int)ErrorCode.StorageProviderBase, $"AWS DynamoDB Grain Storage {name} is initializing: {initMsg}");

                storage = new DynamoDBStorage(
                    logger,
                    options.Service,
                    options.AccessKey,
                    options.SecretKey,
                    options.Token,
                    options.ProfileName,
                    options.ReadCapacityUnits,
                    options.WriteCapacityUnits,
                    options.UseProvisionedThroughput,
                    options.CreateIfNotExists,
                    options.UpdateIfExists);

                await storage.InitializeTable(options.TableName,
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
                    ttlAttributeName: options.TimeToLive.HasValue ? GRAIN_TTL_PROPERTY_NAME : null);
                stopWatch.Stop();
                logger.LogInformation((int)ErrorCode.StorageProviderBase,
                    $"Initializing provider {name} of type {GetType().Name} in stage {options.InitStage} took {stopWatch.ElapsedMilliseconds} Milliseconds.");
            }
            catch (Exception exc)
            {
                stopWatch.Stop();
                logger.LogError((int)ErrorCode.Provider_ErrorFromInit, $"Initialization failed for provider {name} of type {GetType().Name} in stage {options.InitStage} in {stopWatch.ElapsedMilliseconds} Milliseconds.", exc);
                throw;
            }
        }

        /// <summary> Shutdown this storage provider. </summary>
        public Task Close(CancellationToken ct) => Task.CompletedTask;

        /// <summary> Read state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.ReadStateAsync{T}"/>
        public async Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            if (storage == null) throw new ArgumentException("GrainState-Table property not initialized");

            var partitionKey = GetKeyString(grainId);
            if (logger.IsEnabled(LogLevel.Trace))
                logger.LogTrace(
                    (int)ErrorCode.StorageProviderBase,
                    "Reading: GrainType={GrainType} Pk={PartitionKey} GrainId={GrainId} from Table={TableName}",
                    grainType,
                    partitionKey,
                    grainId,
                    options.TableName);

            var rowKey = AWSUtils.ValidateDynamoDBRowKey(grainType);

            var record = await storage.ReadSingleEntryAsync(options.TableName,
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
                        State = fields.TryGetValue(BINARY_STATE_PROPERTY_NAME, out var propertyName) ? propertyName.B?.ToArray() : null,
                    };
                }).ConfigureAwait(false);

            if (record != null)
            {
                var loadedState = ConvertFromStorageFormat<T>(record);
                grainState.RecordExists = loadedState != null;
                grainState.State = loadedState ?? Activator.CreateInstance<T>();
                grainState.ETag = record.ETag.ToString();
            }

            // Else leave grainState in previous default condition
        }

        /// <summary> Write state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.WriteStateAsync{T}"/>
        public async Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            if (storage == null) throw new ArgumentException("GrainState-Table property not initialized");

            var partitionKey = GetKeyString(grainId);
            var rowKey = AWSUtils.ValidateDynamoDBRowKey(grainType);

            var record = new GrainStateRecord { GrainReference = partitionKey, GrainType = rowKey };

            try
            {
                ConvertToStorageFormat(grainState.State, record);
                await WriteStateInternal(grainState, record);
            }
            catch (ConditionalCheckFailedException exc)
            {
                throw new InconsistentStateException($"Inconsistent grain state: {exc}");
            }
            catch (Exception exc)
            {
                logger.LogError(
                    (int)ErrorCode.StorageProviderBase,
                    exc,
                    "Error Writing: GrainType={GrainType} Grainid={GrainId} ETag={ETag} to Table={TableName}",
                    grainType,
                    grainId,
                    grainState.ETag,
                    options.TableName);
                throw;
            }
        }

        private async Task WriteStateInternal<T>(IGrainState<T> grainState, GrainStateRecord record, bool clear = false)
        {
            var fields = new Dictionary<string, AttributeValue>();
            if (options.TimeToLive.HasValue)
            {
                fields.Add(GRAIN_TTL_PROPERTY_NAME, new AttributeValue { N = ((DateTimeOffset)DateTime.UtcNow.Add(options.TimeToLive.Value)).ToUnixTimeSeconds().ToString() });
            }

            if (record.State != null && record.State.Length > 0)
            {
                fields.Add(BINARY_STATE_PROPERTY_NAME, new AttributeValue { B = new MemoryStream(record.State) });
            }
            else
            {
                fields.Add(BINARY_STATE_PROPERTY_NAME, new AttributeValue { NULL = true });
            }

            var newEtag = 0;
            if (clear)
            {
                fields.Add(GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(record.GrainReference));
                fields.Add(GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(record.GrainType));

                int currentEtag;
                int.TryParse(grainState.ETag, out currentEtag);
                newEtag = currentEtag;
                newEtag++;
                fields.Add(ETAG_PROPERTY_NAME, new AttributeValue { N = newEtag.ToString() });

                await storage.PutEntryAsync(options.TableName, fields).ConfigureAwait(false);
            }
            else if (string.IsNullOrWhiteSpace(grainState.ETag))
            {
                fields.Add(GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(record.GrainReference));
                fields.Add(GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(record.GrainType));
                fields.Add(ETAG_PROPERTY_NAME, new AttributeValue { N = "0" });

                var expression = $"attribute_not_exists({GRAIN_REFERENCE_PROPERTY_NAME}) AND attribute_not_exists({GRAIN_TYPE_PROPERTY_NAME})";
                await storage.PutEntryAsync(options.TableName, fields, expression).ConfigureAwait(false);
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
                await storage.UpsertEntryAsync(options.TableName, keys, fields, expression, conditionalValues).ConfigureAwait(false);
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
        /// <see cref="IGrainStorage.ClearStateAsync{T}"/>
        public async Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            if (storage == null) throw new ArgumentException("GrainState-Table property not initialized");

            var partitionKey = GetKeyString(grainId);
            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace(
                    (int)ErrorCode.StorageProviderBase,
                    "Clearing: GrainType={GrainType} Pk={PartitionKey} GrainId={GrainId} ETag={ETag} DeleteStateOnClear={DeleteStateOnClear} from Table={TableName}",
                    grainType,
                    partitionKey,
                    grainId,
                    grainState.ETag,
                    options.DeleteStateOnClear,
                    options.TableName);
            }
            var rowKey = AWSUtils.ValidateDynamoDBRowKey(grainType);
            var record = new GrainStateRecord { GrainReference = partitionKey, ETag = string.IsNullOrWhiteSpace(grainState.ETag) ? 0 : int.Parse(grainState.ETag), GrainType = rowKey };

            var operation = "Clearing";
            try
            {
                if (options.DeleteStateOnClear)
                {
                    operation = "Deleting";
                    var keys = new Dictionary<string, AttributeValue>();
                    keys.Add(GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(record.GrainReference));
                    keys.Add(GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(record.GrainType));

                    await storage.DeleteEntryAsync(options.TableName, keys).ConfigureAwait(false);
                    grainState.ETag = null;
                }
                else
                {
                    await WriteStateInternal(grainState, record, true);
                }
            }
            catch (Exception exc)
            {
                logger.LogError(
                    (int)ErrorCode.StorageProviderBase,
                    exc,
                    "Error {Operation}: GrainType={GrainType} GrainId={GrainId} ETag={ETag} from Table={TableName}",
                    operation,
                    grainType,
                    grainId,
                    grainState.ETag,
                    options.TableName);
                throw;
            }
        }

        internal class GrainStateRecord
        {
            public string GrainReference { get; set; } = "";
            public string GrainType { get; set; } = "";
            public byte[] State { get; set; }
            public int ETag { get; set; }
        }

        private string GetKeyString(GrainId grainId)
        {
            var key = $"{options.ServiceId}_{grainId}";
            return AWSUtils.ValidateDynamoDBPartitionKey(key);
        }

        internal T ConvertFromStorageFormat<T>(GrainStateRecord entity)
        {
            T dataValue = default;
            try
            {
                dataValue = options.GrainStorageSerializer.Deserialize<T>(entity.State);
            }
            catch (Exception exc)
            {
                var sb = new StringBuilder();
                sb.AppendFormat("Unable to convert from storage format GrainStateEntity.Data={0}", entity.State);

                if (dataValue != null)
                {
                    sb.Append($"Data Value={dataValue} Type={dataValue.GetType()}");
                }

                var message = sb.ToString();
                logger.LogError(exc, "{Message}", message);
                throw new AggregateException(message, exc);
            }

            return dataValue;
        }

        internal void ConvertToStorageFormat(object grainState, GrainStateRecord entity)
        {
            int dataSize;
            // Convert to binary format
            entity.State = options.GrainStorageSerializer.Serialize(grainState).ToArray();
            dataSize = BINARY_STATE_PROPERTY_NAME.Length + entity.State.Length;

            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("Writing binary data size = {DataSize} for grain id = Partition={Partition} / Row={Row}",
                dataSize, entity.GrainReference, entity.GrainType);

            var pkSize = GRAIN_REFERENCE_PROPERTY_NAME.Length + entity.GrainReference.Length;
            var rkSize = GRAIN_TYPE_PROPERTY_NAME.Length + entity.GrainType.Length;
            var versionSize = ETAG_PROPERTY_NAME.Length + entity.ETag.ToString().Length;

            if ((pkSize + rkSize + versionSize + dataSize) > MAX_DATA_SIZE)
            {
                var msg = $"Data too large to write to DynamoDB table. Size={dataSize} MaxSize={MAX_DATA_SIZE}";
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
