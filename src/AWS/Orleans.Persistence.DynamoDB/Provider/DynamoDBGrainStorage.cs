using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Persistence.DynamoDB;
using Orleans.Runtime;
using Orleans.Serialization;
using System.Linq;
using Orleans.Persistence.DynamoDB.Provider.Compression.Interfaces;
using Orleans.Persistence.DynamoDB.Provider.Serialization.Interfaces;

namespace Orleans.Storage
{
    /// <summary>
    /// Dynamo DB storage Provider.
    /// Persist Grain State in a DynamoDB table either in Json or Binary format.
    /// </summary>
    public class DynamoDBGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
    {
        private const int MaxDataSize = 400 * 1024;
        private const string GrainReferencePropertyName = "GrainReference";
        private const string StringStatePropertyName = "StringState";
        private const string BinaryStatePropertyName = "BinaryState";
        private const string BinaryStatePropertiesPropertyName = "BinaryStateProperties";
        private const string GrainTypePropertyName = "GrainType";
        private const string EtagPropertyName = "ETag";
        private const string GrainTtlPropertyName = "GrainTtl";
        private const string CurrentEtagAlias = ":currentETag";

        private readonly DynamoDBStorageOptions options;
        private readonly SerializationManager serializationManager;
        private readonly ILogger logger;
        private readonly IGrainFactory grainFactory;
        private readonly ITypeResolver typeResolver;
        private readonly IGrainStateSerializationManager grainStateSerializationManager;
        private readonly IGrainStateCompressionManager grainStateCompressionManager;
        private readonly string name;

        private DynamoDBStorage storage;
        private JsonSerializerSettings jsonSettings;

        /// <summary>
        /// Default Constructor
        /// </summary>
        public DynamoDBGrainStorage(
            string name,
            DynamoDBStorageOptions options,
            SerializationManager serializationManager,
            IGrainFactory grainFactory,
            ITypeResolver typeResolver,
            IGrainStateSerializationManager grainStateSerializationManager,
            IGrainStateCompressionManager grainStateCompressionManager,
            ILogger<DynamoDBGrainStorage> logger)
        {
            this.name = name;
            this.logger = logger;
            this.options = options;
            this.serializationManager = serializationManager;
            this.grainFactory = grainFactory;
            this.typeResolver = typeResolver;
            this.grainStateSerializationManager = grainStateSerializationManager;
            this.grainStateCompressionManager = grainStateCompressionManager;
        }

        public void Participate(ISiloLifecycle lifecycle) =>
            lifecycle.Subscribe(
                OptionFormattingUtilities.Name<DynamoDBGrainStorage>(this.name),
                this.options.InitStage,
                this.Init,
                this.Close);

        /// <summary> Initialization function for this storage provider. </summary>
        public async Task Init(CancellationToken ct)
        {
            var stopWatch = Stopwatch.StartNew();

            try
            {
                var initMsg = string.Format(
                    "Init: Name={0} ServiceId={1} Table={2} DeleteStateOnClear={3}, UseJson={4}, StateCompressionPolicy.IsEnabled={5}, StateCompressionPolicy.CompressStateIfAboveByteCount={6}, StateCompressionPolicy.Compression={7}, StateCompressionPolicy.CompressionLevel={8}",
                    this.name,
                    this.options.ServiceId,
                    this.options.TableName,
                    this.options.DeleteStateOnClear,
                    this.options.UseJson,
                    this.options.StateCompressionPolicy?.IsEnabled,
                    this.options.StateCompressionPolicy?.CompressStateIfAboveByteCount,
                    this.options.StateCompressionPolicy?.Compression,
                    this.options.StateCompressionPolicy?.CompressionLevel);

                this.jsonSettings = OrleansJsonSerializer.UpdateSerializerSettings(
                    OrleansJsonSerializer.GetDefaultSerializerSettings(this.typeResolver, this.grainFactory),
                    this.options.UseFullAssemblyNames, this.options.IndentJson, this.options.TypeNameHandling);
                this.options.ConfigureJsonSerializerSettings?.Invoke(this.jsonSettings);

                this.logger.LogInformation((int)ErrorCode.StorageProviderBase,
                    $"AWS DynamoDB Grain Storage {this.name} is initializing: {initMsg}");

                this.storage = new DynamoDBStorage(this.logger, this.options.Service, this.options.AccessKey,
                    this.options.SecretKey,
                    this.options.ReadCapacityUnits, this.options.WriteCapacityUnits,
                    this.options.UseProvisionedThroughput,
                    this.options.CreateIfNotExists, this.options.UpdateIfExists);

                await this.storage.InitializeTable(this.options.TableName,
                    new List<KeySchemaElement>
                    {
                        new()
                        {
                            AttributeName = GrainReferencePropertyName, KeyType = KeyType.HASH
                        },
                        new()
                        {
                            AttributeName = GrainTypePropertyName, KeyType = KeyType.RANGE
                        }
                    },
                    new List<AttributeDefinition>
                    {
                        new()
                        {
                            AttributeName = GrainReferencePropertyName, AttributeType = ScalarAttributeType.S
                        },
                        new()
                        {
                            AttributeName = GrainTypePropertyName, AttributeType = ScalarAttributeType.S
                        }
                    },
                    secondaryIndexes: null,
                    ttlAttributeName: this.options.TimeToLive.HasValue ? GrainTtlPropertyName : null);
                stopWatch.Stop();
                this.logger.LogInformation((int)ErrorCode.StorageProviderBase,
                    $"Initializing provider {this.name} of type {this.GetType().Name} in stage {this.options.InitStage} took {stopWatch.ElapsedMilliseconds} Milliseconds.");
            }
            catch (Exception exc)
            {
                stopWatch.Stop();
                this.logger.LogError((int)ErrorCode.Provider_ErrorFromInit,
                    $"Initialization failed for provider {this.name} of type {this.GetType().Name} in stage {this.options.InitStage} in {stopWatch.ElapsedMilliseconds} Milliseconds.",
                    exc);
                throw;
            }
        }

        /// <summary> Shutdown this storage provider. </summary>
        public Task Close(CancellationToken ct) => Task.CompletedTask;

        /// <summary> Read state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.ReadStateAsync"/>
        public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            if (this.storage is null) throw new ArgumentException("GrainState-Table property not initialized");

            var partitionKey = this.GetKeyString(grainReference);
            if (this.logger.IsEnabled(LogLevel.Trace))
                this.logger.Trace(ErrorCode.StorageProviderBase,
                    "Reading: GrainType={0} Pk={1} GrainId={2} from Table={3}",
                    grainType, partitionKey, grainReference, this.options.TableName);

            var rowKey = AWSUtils.ValidateDynamoDBRowKey(grainType);

            var record = await this.storage.ReadSingleEntryAsync(this.options.TableName,
                new Dictionary<string, AttributeValue>
                {
                    {GrainReferencePropertyName, new AttributeValue(partitionKey)},
                    {GrainTypePropertyName, new AttributeValue(rowKey)}
                },
                (fields) =>
                {
                    return new GrainStateRecord
                    {
                        GrainType = fields[GrainTypePropertyName].S,
                        GrainReference = fields[GrainReferencePropertyName].S,
                        ETag = int.Parse(fields[EtagPropertyName].N),
                        BinaryState =
                            fields.ContainsKey(BinaryStatePropertyName)
                                ? fields[BinaryStatePropertyName].B.ToArray()
                                : null,
                        StringState = fields.ContainsKey(StringStatePropertyName)
                            ? fields[StringStatePropertyName].S
                            : string.Empty,
                        BinaryStateProperties = fields.ContainsKey(BinaryStatePropertiesPropertyName)
                            ? fields[BinaryStatePropertiesPropertyName].M
                                .ToDictionary(
                                    item => item.Key,
                                    item => item.Value.S)
                            : null
                    };
                }).ConfigureAwait(false);

            if (record is not null)
            {
                var loadedState = this.ConvertFromStorageFormat(record, grainState.Type);
                grainState.RecordExists = loadedState is not null;
                grainState.State = loadedState ?? Activator.CreateInstance(grainState.Type);
                grainState.ETag = record.ETag.ToString();
            }

            // Else leave grainState in previous default condition
        }

        /// <summary> Write state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.WriteStateAsync"/>
        public async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            if (this.storage is null) throw new ArgumentException("GrainState-Table property not initialized");

            var partitionKey = this.GetKeyString(grainReference);
            var rowKey = AWSUtils.ValidateDynamoDBRowKey(grainType);

            var record = new GrainStateRecord
            {
                GrainReference = partitionKey,
                GrainType = rowKey,
                BinaryStateProperties = new Dictionary<string, string>()
            };

            try
            {
                this.ConvertToStorageFormat(grainState.State, record);
                await this.WriteStateInternal(grainState, record);
            }
            catch (ConditionalCheckFailedException exc)
            {
                throw new InconsistentStateException("Invalid grain state", exc);
            }
            catch (Exception exc)
            {
                this.logger.Error(ErrorCode.StorageProviderBase,
                    string.Format("Error Writing: GrainType={0} GrainId={1} ETag={2} to Table={3} Exception={4}",
                        grainType, grainReference, grainState.ETag, this.options.TableName, exc.Message), exc);
                throw;
            }
        }

        private async Task WriteStateInternal(IGrainState grainState, GrainStateRecord record, bool clear = false)
        {
            var fields = new Dictionary<string, AttributeValue>();
            if (this.options.TimeToLive.HasValue)
            {
                fields.Add(GrainTtlPropertyName,
                    new AttributeValue
                    {
                        N = ((DateTimeOffset)DateTime.UtcNow.Add(this.options.TimeToLive.Value)).ToUnixTimeSeconds()
                            .ToString()
                    });
            }

            if (record.BinaryState is {Length: > 0})
            {
                fields.Add(BinaryStatePropertyName, new AttributeValue { B = new MemoryStream(record.BinaryState) });
            }
            else if (!string.IsNullOrWhiteSpace(record.StringState))
            {
                fields.Add(StringStatePropertyName, new AttributeValue(record.StringState));
            }

            if (record.BinaryStateProperties is not null)
            {
                fields.Add(BinaryStatePropertiesPropertyName, new AttributeValue
                {
                    M = record.BinaryStateProperties.ToDictionary(
                        item => item.Key,
                        item => new AttributeValue(item.Value))
                });
            }

            var newEtag = 0;
            if (clear)
            {
                fields.Add(GrainReferencePropertyName, new AttributeValue(record.GrainReference));
                fields.Add(GrainTypePropertyName, new AttributeValue(record.GrainType));

                int.TryParse(grainState.ETag, out var currentEtag);
                newEtag = currentEtag;
                fields.Add(EtagPropertyName, new AttributeValue { N = newEtag++.ToString() });

                await this.storage.PutEntryAsync(this.options.TableName, fields).ConfigureAwait(false);
            }
            else if (string.IsNullOrWhiteSpace(grainState.ETag))
            {
                fields.Add(GrainReferencePropertyName, new AttributeValue(record.GrainReference));
                fields.Add(GrainTypePropertyName, new AttributeValue(record.GrainType));
                fields.Add(EtagPropertyName, new AttributeValue { N = "0" });

                var expression =
                    $"attribute_not_exists({GrainReferencePropertyName}) AND attribute_not_exists({GrainTypePropertyName})";
                await this.storage.PutEntryAsync(this.options.TableName, fields, expression).ConfigureAwait(false);
            }
            else
            {
                var keys = new Dictionary<string, AttributeValue>
                {
                    {GrainReferencePropertyName, new AttributeValue(record.GrainReference)},
                    {GrainTypePropertyName, new AttributeValue(record.GrainType)}
                };

                int.TryParse(grainState.ETag, out var currentEtag);
                newEtag = currentEtag;
                newEtag++;
                fields.Add(EtagPropertyName, new AttributeValue { N = newEtag.ToString() });

                var conditionalValues = new Dictionary<string, AttributeValue>
                {
                    {CurrentEtagAlias, new AttributeValue {N = currentEtag.ToString()}}
                };
                var expression = $"{EtagPropertyName} = {CurrentEtagAlias}";
                await this.storage.UpsertEntryAsync(this.options.TableName, keys, fields, expression, conditionalValues)
                    .ConfigureAwait(false);
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
            if (this.storage is null) throw new ArgumentException("GrainState-Table property not initialized");

            var partitionKey = this.GetKeyString(grainReference);
            if (this.logger.IsEnabled(LogLevel.Trace))
            {
                this.logger.Trace(ErrorCode.StorageProviderBase,
                    "Clearing: GrainType={0} Pk={1} GrainId={2} ETag={3} DeleteStateOnClear={4} from Table={5}",
                    grainType, partitionKey, grainReference, grainState.ETag, this.options.DeleteStateOnClear,
                    this.options.TableName);
            }

            var rowKey = AWSUtils.ValidateDynamoDBRowKey(grainType);
            var record = new GrainStateRecord
            {
                GrainReference = partitionKey,
                ETag = string.IsNullOrWhiteSpace(grainState.ETag) ? 0 : int.Parse(grainState.ETag),
                GrainType = rowKey
            };

            var operation = "Clearing";
            try
            {
                if (this.options.DeleteStateOnClear)
                {
                    operation = "Deleting";
                    var keys = new Dictionary<string, AttributeValue>
                    {
                        {GrainReferencePropertyName, new AttributeValue(record.GrainReference)},
                        {GrainTypePropertyName, new AttributeValue(record.GrainType)}
                    };

                    await this.storage.DeleteEntryAsync(this.options.TableName, keys).ConfigureAwait(false);
                    grainState.ETag = string.Empty;
                }
                else
                {
                    await this.WriteStateInternal(grainState, record, true);
                }
            }
            catch (Exception exc)
            {
                this.logger.Error(ErrorCode.StorageProviderBase, string.Format(
                    "Error {0}: GrainType={1} GrainId={2} ETag={3} from Table={4} Exception={5}",
                    operation, grainType, grainReference, grainState.ETag, this.options.TableName, exc.Message), exc);
                throw;
            }
        }

        private string GetKeyString(GrainReference grainReference)
        {
            var key = $"{this.options.ServiceId}_{grainReference.ToKeyString()}";
            return AWSUtils.ValidateDynamoDBPartitionKey(key);
        }

        internal object ConvertFromStorageFormat(GrainStateRecord entity, Type stateType)
        {
            var binaryData = entity.BinaryState;
            var stringData = entity.StringState;

            object dataValue = null;
            try
            {
                if (entity.BinaryStateProperties == null || entity.BinaryStateProperties.Count == 0)
                {
                    dataValue = this.ConvertFromStorageFormatLegacy(stateType, binaryData, stringData);
                }
                else
                {
                    this.grainStateCompressionManager.Decompress(entity);
                    dataValue = this.grainStateSerializationManager.Deserialize(stateType, entity);
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
                if (dataValue is not null)
                {
                    sb.AppendFormat("Data Value={0} Type={1}", dataValue, dataValue.GetType());
                }

                this.logger.Error(0, sb.ToString(), exc);
                throw new AggregateException(sb.ToString(), exc);
            }

            return dataValue;
        }

        private object ConvertFromStorageFormatLegacy(Type stateType, byte[] binaryData, string stringData)
        {
            object ret = null;

            if (binaryData?.Length > 0)
            {
                // Rehydrate
                ret = this.serializationManager.DeserializeFromByteArray<object>(binaryData);
            }
            else if (!string.IsNullOrEmpty(stringData))
            {
                ret = JsonConvert.DeserializeObject(stringData, stateType, this.jsonSettings);
            }

            return ret;
        }

        internal void ConvertToStorageFormat(object grainState, GrainStateRecord entity)
        {
            this.grainStateSerializationManager.Serialize(grainState, entity);
            this.grainStateCompressionManager.Compress(entity);

            var dataSize = StringStatePropertyName.Length + entity.BinaryState.Length;

            if (this.logger.IsEnabled(LogLevel.Trace))
            {
                this.logger.Trace(
                    "Writing data size = {0} for grain id = Partition={1} / Row={2}",
                    dataSize, entity.GrainReference, entity.GrainType);
            }

            var pkSize = GrainReferencePropertyName.Length + entity.GrainReference.Length;
            var rkSize = GrainTypePropertyName.Length + entity.GrainType.Length;
            var versionSize = EtagPropertyName.Length + entity.ETag.ToString().Length;

            if ((pkSize + rkSize + versionSize + dataSize) > MaxDataSize)
            {
                var msg = $"Data too large to write to DynamoDB table. Size={dataSize} MaxSize={MaxDataSize}";
                throw new ArgumentOutOfRangeException(nameof(entity), msg);
            }
        }

        public class GrainStateRecord
        {
            public string GrainReference { get; set; } = string.Empty;

            public string GrainType { get; set; } = string.Empty;

            public byte[] BinaryState { get; set; }

            // Legacy. This property is kept for legacy and migration purposes
            public string StringState { get; set; }

            // The grain state properties
            public Dictionary<string, string> BinaryStateProperties { get; set; }

            public int ETag { get; set; }
        }
    }
}
