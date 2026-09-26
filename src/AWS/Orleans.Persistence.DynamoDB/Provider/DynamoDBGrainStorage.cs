using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
using Orleans.Serialization.Serializers;

namespace Orleans.Storage
{
    /// <summary>
    /// Dynamo DB storage Provider.
    /// Persist Grain State in a DynamoDB table either in Json or Binary format.
    /// </summary>
    public partial class DynamoDBGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
    {
        private const int MAX_DATA_SIZE = 400 * 1024;
        private const string GRAIN_REFERENCE_PROPERTY_NAME = "GrainReference";
        private const string BINARY_STATE_PROPERTY_NAME = "GrainState";
        private const string GRAIN_TYPE_PROPERTY_NAME = "GrainType";
        private const string ETAG_PROPERTY_NAME = "ETag";
        private const string GRAIN_TTL_PROPERTY_NAME = "GrainTtl";
        private const string CURRENT_ETAG_ALIAS = ":currentETag";
        private const string KEY_FORMAT_MARKER = "__OrleansKeyFormat";
        private const string KEY_FORMAT_PROPERTY_NAME = "KeyFormat";
        private const string LEGACY_KEY_FORMAT = "EmptyServiceId";
        private const string CLUSTER_KEY_FORMAT = "ClusterServiceId";
        private const string MIGRATING_KEY_FORMAT = "MigratingToClusterServiceId";

        // prefixes the ETag of a state read from its legacy key, which its next write or clear moves
        private const string LEGACY_ETAG_PREFIX = "legacy:";

        private readonly DynamoDBStorageOptions options;
        private readonly IActivatorProvider _activatorProvider;
        private readonly ILogger logger;
        private readonly string name;

        private DynamoDBStorage storage = null!;
        private string _keyServiceId = string.Empty;
        private bool _migrateLegacyKeys;

        /// <summary>
        /// Default Constructor
        /// </summary>
        public DynamoDBGrainStorage(
            string name,
            DynamoDBStorageOptions options,
            IActivatorProvider activatorProvider,
            ILogger<DynamoDBGrainStorage> logger)
        {
            this.name = name;
            this.logger = logger;
            this.options = options;
            _activatorProvider = activatorProvider;
        }

        /// <inheritdoc/>
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
                var initMsg = string.Format(CultureInfo.CurrentCulture, "Init: Name={0} ServiceId={1} Table={2} DeleteStateOnClear={3}",
                        this.name, this.options.ServiceId, this.options.TableName, this.options.DeleteStateOnClear);

                LogInformationInitializingDynamoDBGrainStorage(logger, this.name, initMsg);

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
                    ttlAttributeName: this.options.TimeToLive.HasValue ? GRAIN_TTL_PROPERTY_NAME : null,
                    cancellationToken: ct);
                await ResolveKeyFormatAsync(ct);
                stopWatch.Stop();
                LogInformationProviderInitialized(logger, this.name, this.GetType().Name, this.options.InitStage, stopWatch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exc)
            {
                stopWatch.Stop();
                LogErrorProviderInitFailed(logger, this.name, this.GetType().Name, this.options.InitStage, stopWatch.ElapsedMilliseconds, exc);
                throw;
            }
        }

        /// <summary> Shutdown this storage provider. </summary>
        public Task Close(CancellationToken ct) => Task.CompletedTask;

        /// <summary> Read state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.ReadStateAsync{T}(string, GrainId, IGrainState{T})"/>
        public async Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            if (this.storage == null) throw new ArgumentException("GrainState-Table property not initialized");

            string partitionKey = GetKeyString(grainId);
            LogTraceReadingGrainState(logger, grainType, partitionKey, grainId, this.options.TableName);

            string rowKey = AWSUtils.ValidateDynamoDBRowKey(grainType);
            var record = await ReadRecordAsync(partitionKey, rowKey);

            if (record == null && _migrateLegacyKeys)
            {
                record = await ReadRecordAsync(GetLegacyKeyString(grainId), rowKey);
                if (record != null)
                {
                    LogWarningReadingLegacyKey(logger, grainType, grainId, this.options.TableName);
                }
            }

            if (record != null)
            {
                var loadedState = ConvertFromStorageFormat<T>(record);
                grainState.RecordExists = loadedState != null;
                grainState.State = loadedState ?? CreateInstance<T>();
                grainState.ETag = record.GrainReference == partitionKey
                    ? record.ETag.ToString(CultureInfo.InvariantCulture)
                    : LEGACY_ETAG_PREFIX + record.ETag.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                ResetGrainState(grainState);
            }
        }

        /// <summary> Write state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.WriteStateAsync{T}(string, GrainId, IGrainState{T})"/>
        public async Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            if (this.storage == null) throw new ArgumentException("GrainState-Table property not initialized");

            string partitionKey = GetKeyString(grainId);
            string rowKey = AWSUtils.ValidateDynamoDBRowKey(grainType);

            var record = new GrainStateRecord { GrainReference = partitionKey, GrainType = rowKey };

            try
            {
                ConvertToStorageFormat(grainState.State, record);
                if (TryParseLegacyETag(grainState.ETag, out var legacyETag))
                {
                    await MigrateStateAsync(grainState, record, GetLegacyKeyString(grainId), legacyETag, clear: false);
                }
                else
                {
                    await WriteStateInternal(grainState, record);
                }
            }
            catch (ConditionalCheckFailedException exc)
            {
                throw new InconsistentStateException($"Inconsistent grain state: {exc}");
            }
            catch (TransactionCanceledException exc) when (IsConditionalCheckFailure(exc))
            {
                throw new InconsistentStateException($"Inconsistent grain state: {exc}");
            }
            catch (Exception exc)
            {
                LogErrorWritingGrainState(logger, exc, grainType, grainId, grainState.ETag, this.options.TableName);
                throw;
            }
        }

        private async Task WriteStateInternal<T>(IGrainState<T> grainState, GrainStateRecord record, bool clear = false)
        {
            var fields = new Dictionary<string, AttributeValue>();
            if (this.options.TimeToLive.HasValue)
            {
                fields.Add(GRAIN_TTL_PROPERTY_NAME, new AttributeValue { N = ((DateTimeOffset)DateTime.UtcNow.Add(this.options.TimeToLive.Value)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) });
            }

            if (record.State != null && record.State.Length > 0)
            {
                fields.Add(BINARY_STATE_PROPERTY_NAME, new AttributeValue { B = new MemoryStream(record.State) });
            }
            else
            {
                fields.Add(BINARY_STATE_PROPERTY_NAME, new AttributeValue { NULL = true });
            }

            int newEtag = 0;
            if (clear)
            {
                int currentEtag;
                int.TryParse(grainState.ETag, NumberStyles.Integer, CultureInfo.InvariantCulture, out currentEtag);
                newEtag = currentEtag;
                newEtag++;
                fields.Add(ETAG_PROPERTY_NAME, new AttributeValue { N = newEtag.ToString(CultureInfo.InvariantCulture) });

                if (string.IsNullOrWhiteSpace(grainState.ETag))
                {
                    fields.Add(GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(record.GrainReference));
                    fields.Add(GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(record.GrainType));
                    var expression = $"attribute_not_exists({GRAIN_REFERENCE_PROPERTY_NAME}) AND attribute_not_exists({GRAIN_TYPE_PROPERTY_NAME})";
                    await this.storage.PutEntryAsync(this.options.TableName, fields, expression).ConfigureAwait(false);
                }
                else
                {
                    var keys = new Dictionary<string, AttributeValue>
                    {
                        { GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(record.GrainReference) },
                        { GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(record.GrainType) }
                    };
                    var conditionalValues = new Dictionary<string, AttributeValue> { { CURRENT_ETAG_ALIAS, new AttributeValue { N = currentEtag.ToString(CultureInfo.InvariantCulture) } } };
                    var expression = $"{ETAG_PROPERTY_NAME} = {CURRENT_ETAG_ALIAS}";
                    await this.storage.UpsertEntryAsync(this.options.TableName, keys, fields, expression, conditionalValues).ConfigureAwait(false);
                }
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
                int.TryParse(grainState.ETag, NumberStyles.Integer, CultureInfo.InvariantCulture, out currentEtag);
                newEtag = currentEtag;
                newEtag++;
                fields.Add(ETAG_PROPERTY_NAME, new AttributeValue { N = newEtag.ToString(CultureInfo.InvariantCulture) });

                var conditionalValues = new Dictionary<string, AttributeValue> { { CURRENT_ETAG_ALIAS, new AttributeValue { N = currentEtag.ToString(CultureInfo.InvariantCulture) } } };
                var expression = $"{ETAG_PROPERTY_NAME} = {CURRENT_ETAG_ALIAS}";
                await this.storage.UpsertEntryAsync(this.options.TableName, keys, fields, expression, conditionalValues).ConfigureAwait(false);
            }

            grainState.ETag = newEtag.ToString(CultureInfo.InvariantCulture);
            grainState.RecordExists = !clear;
        }

        /// <summary> Clear / Delete state data function for this storage provider. </summary>
        /// <remarks>
        /// If the <c>DeleteStateOnClear</c> is set to <c>true</c> then the table row
        /// for this grain will be deleted / removed, otherwise the table row will be
        /// cleared by overwriting with default / null values.
        /// </remarks>
        /// <see cref="IGrainStorage.ClearStateAsync{T}(string, GrainId, IGrainState{T})"/>
        public async Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            if (this.storage == null) throw new ArgumentException("GrainState-Table property not initialized");

            string partitionKey = GetKeyString(grainId);
            LogTraceClearingGrainState(logger, grainType, partitionKey, grainId, grainState.ETag, this.options.DeleteStateOnClear, this.options.TableName);

            string rowKey = AWSUtils.ValidateDynamoDBRowKey(grainType);
            var fromLegacyKey = TryParseLegacyETag(grainState.ETag, out var legacyETag);
            var record = new GrainStateRecord
            {
                GrainReference = partitionKey,
                ETag = fromLegacyKey ? legacyETag
                    : string.IsNullOrWhiteSpace(grainState.ETag) ? 0
                    : int.Parse(grainState.ETag, NumberStyles.Integer, CultureInfo.InvariantCulture),
                GrainType = rowKey
            };

            var operation = "Clearing";
            try
            {
                if (fromLegacyKey)
                {
                    operation = "Migrating";
                    if (this.options.DeleteStateOnClear)
                    {
                        await DeleteLegacyRecordAsync(partitionKey, GetLegacyKeyString(grainId), rowKey, legacyETag);
                        ResetGrainState(grainState);
                    }
                    else
                    {
                        await MigrateStateAsync(grainState, record, GetLegacyKeyString(grainId), legacyETag, clear: true);
                        grainState.State = CreateInstance<T>();
                        grainState.RecordExists = false;
                    }
                }
                else if (this.options.DeleteStateOnClear)
                {
                    operation = "Deleting";
                    var keys = new Dictionary<string, AttributeValue>
                    {
                        { GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(record.GrainReference) },
                        { GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(record.GrainType) }
                    };
                    var expression = $"attribute_not_exists({ETAG_PROPERTY_NAME})";
                    Dictionary<string, AttributeValue>? conditionalValues = null;
                    if (!string.IsNullOrWhiteSpace(grainState.ETag))
                    {
                        conditionalValues = new Dictionary<string, AttributeValue> { { CURRENT_ETAG_ALIAS, new AttributeValue { N = record.ETag.ToString(CultureInfo.InvariantCulture) } } };
                        expression = $"{ETAG_PROPERTY_NAME} = {CURRENT_ETAG_ALIAS}";
                    }

                    await this.storage.DeleteEntryAsync(this.options.TableName, keys, expression, conditionalValues).ConfigureAwait(false);
                    ResetGrainState(grainState);
                }
                else
                {
                    await WriteStateInternal(grainState, record, true);
                    grainState.State = CreateInstance<T>();
                    grainState.RecordExists = false;
                }
            }
            catch (TransactionCanceledException exc) when (IsConditionalCheckFailure(exc))
            {
                throw new InconsistentStateException($"Inconsistent grain state: {exc}");
            }
            catch (ConditionalCheckFailedException exc)
            {
                throw new InconsistentStateException($"Inconsistent grain state: {exc}");
            }
            catch (Exception exc)
            {
                LogErrorClearingGrainState(logger, exc, operation, grainType, grainId, grainState.ETag, this.options.TableName);
                throw;
            }
        }

        internal class GrainStateRecord
        {
            public string GrainReference { get; set; } = "";
            public string GrainType { get; set; } = "";
            public byte[]? State { get; set; }
            public int ETag { get; set; }
        }

        private string GetKeyString(GrainId grainId)
        {
            var key = $"{_keyServiceId}_{grainId}";
            return AWSUtils.ValidateDynamoDBPartitionKey(key);
        }

        private static string GetLegacyKeyString(GrainId grainId) => AWSUtils.ValidateDynamoDBPartitionKey($"_{grainId}");

        private Task<GrainStateRecord?> ReadRecordAsync(string partitionKey, string rowKey) =>
            this.storage.ReadSingleEntryAsync(this.options.TableName,
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
                        ETag = int.Parse(fields[ETAG_PROPERTY_NAME].N, NumberStyles.Integer, CultureInfo.InvariantCulture),
                        State = fields.TryGetValue(BINARY_STATE_PROPERTY_NAME, out var propertyName) ? propertyName.B?.ToArray() : null,
                    };
                });

        /// <summary>
        /// Decides the <see cref="DynamoDBStorageOptions.ServiceId"/> the keys are built from. An explicit one is used as
        /// it is. An empty one is kept for compatibility, with a warning, unless the options or the key format recorded in
        /// the table ask for <see cref="ClusterOptions.ServiceId"/>; the choice is recorded in the table.
        /// </summary>
        private async Task ResolveKeyFormatAsync(CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(this.options.ServiceId))
            {
                _keyServiceId = this.options.ServiceId;
                return;
            }

            var recordedFormat = await ReadKeyFormatAsync(ct);
            var useClusterServiceId = this.options.UseClusterServiceId ?? recordedFormat is CLUSTER_KEY_FORMAT or MIGRATING_KEY_FORMAT;

            _keyServiceId = useClusterServiceId ? this.options.ClusterServiceId : string.Empty;
            _migrateLegacyKeys = useClusterServiceId && (this.options.MigrateLegacyKeys ?? recordedFormat == MIGRATING_KEY_FORMAT);

            if (!useClusterServiceId)
            {
                LogWarningEmptyServiceId(logger, this.name, this.options.TableName);
            }

            var format = !useClusterServiceId ? LEGACY_KEY_FORMAT
                : _migrateLegacyKeys ? MIGRATING_KEY_FORMAT
                : CLUSTER_KEY_FORMAT;
            if (recordedFormat != format)
            {
                await this.storage.PutEntryAsync(this.options.TableName, new Dictionary<string, AttributeValue>
                {
                    { GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(KEY_FORMAT_MARKER) },
                    { GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(KEY_FORMAT_MARKER) },
                    { KEY_FORMAT_PROPERTY_NAME, new AttributeValue(format) },
                }, ct);
            }
        }

        private async Task<string?> ReadKeyFormatAsync(CancellationToken ct)
        {
            var marker = await this.storage.ReadSingleEntryAsync(this.options.TableName,
                new Dictionary<string, AttributeValue>
                {
                    { GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(KEY_FORMAT_MARKER) },
                    { GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(KEY_FORMAT_MARKER) }
                },
                fields => fields.TryGetValue(KEY_FORMAT_PROPERTY_NAME, out var value) ? value.S : string.Empty,
                ct);

            return string.IsNullOrEmpty(marker) ? null : marker;
        }

        /// <summary>
        /// Writes the state of a grain read from its legacy key under its current key, and deletes the legacy record in the
        /// same transaction, so that the grain never has both or neither.
        /// </summary>
        private async Task MigrateStateAsync<T>(IGrainState<T> grainState, GrainStateRecord record, string legacyPartitionKey, int legacyETag, bool clear)
        {
            var newETag = legacyETag + 1;
            var fields = new Dictionary<string, AttributeValue>
            {
                { GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(record.GrainReference) },
                { GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(record.GrainType) },
                { ETAG_PROPERTY_NAME, new AttributeValue { N = newETag.ToString(CultureInfo.InvariantCulture) } },
                { BINARY_STATE_PROPERTY_NAME, record.State is { Length: > 0 } ? new AttributeValue { B = new MemoryStream(record.State) } : new AttributeValue { NULL = true } },
            };
            if (this.options.TimeToLive.HasValue)
            {
                fields.Add(GRAIN_TTL_PROPERTY_NAME, new AttributeValue { N = ((DateTimeOffset)DateTime.UtcNow.Add(this.options.TimeToLive.Value)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) });
            }
            else if (await ReadLegacyTtlAsync(legacyPartitionKey, record.GrainType) is { } legacyTtl)
            {
                // a write without TimeToLive leaves the expiry of an item as it is, and so does the migration; the
                // transaction fails if the legacy item was written since, so the expiry read here is the current one
                fields.Add(GRAIN_TTL_PROPERTY_NAME, legacyTtl);
            }

            await this.storage.WriteTxAsync(
                puts: [new Put
                {
                    TableName = this.options.TableName,
                    Item = fields,
                    ConditionExpression = $"attribute_not_exists({GRAIN_REFERENCE_PROPERTY_NAME}) AND attribute_not_exists({GRAIN_TYPE_PROPERTY_NAME})",
                }],
                deletes: [LegacyDelete(legacyPartitionKey, record.GrainType, legacyETag)]);

            grainState.ETag = newETag.ToString(CultureInfo.InvariantCulture);
            grainState.RecordExists = !clear;
        }

        /// <summary>The ETag of a state read from its legacy key, which its next write or clear moves.</summary>
        private static bool TryParseLegacyETag(string? eTag, out int legacyETag)
        {
            legacyETag = 0;
            return eTag is not null
                && eTag.StartsWith(LEGACY_ETAG_PREFIX, StringComparison.Ordinal)
                && int.TryParse(eTag.AsSpan(LEGACY_ETAG_PREFIX.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out legacyETag);
        }

        private async Task<AttributeValue?> ReadLegacyTtlAsync(string legacyPartitionKey, string rowKey)
        {
            var fields = await this.storage.ReadSingleEntryAsync(this.options.TableName,
                new Dictionary<string, AttributeValue>
                {
                    { GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(legacyPartitionKey) },
                    { GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(rowKey) }
                },
                fields => fields);

            return fields is not null && fields.TryGetValue(GRAIN_TTL_PROPERTY_NAME, out var ttl) ? ttl : null;
        }

        private static bool IsConditionalCheckFailure(TransactionCanceledException exception) =>
            exception.CancellationReasons?.Any(reason => string.Equals(reason.Code, "ConditionalCheckFailed", StringComparison.Ordinal)) is true;

        private Task DeleteLegacyRecordAsync(string partitionKey, string legacyPartitionKey, string rowKey, int legacyETag) =>
            this.storage.WriteTxAsync(
                deletes: [LegacyDelete(legacyPartitionKey, rowKey, legacyETag)],
                conditionChecks: [new ConditionCheck
                {
                    TableName = this.options.TableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        { GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(partitionKey) },
                        { GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(rowKey) }
                    },
                    ConditionExpression = $"attribute_not_exists({GRAIN_REFERENCE_PROPERTY_NAME})",
                }]);

        private Delete LegacyDelete(string legacyPartitionKey, string rowKey, int legacyETag) => new()
        {
            TableName = this.options.TableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(legacyPartitionKey) },
                { GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(rowKey) }
            },
            ConditionExpression = $"{ETAG_PROPERTY_NAME} = {CURRENT_ETAG_ALIAS}",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { { CURRENT_ETAG_ALIAS, new AttributeValue { N = legacyETag.ToString(CultureInfo.InvariantCulture) } } },
        };

        internal T? ConvertFromStorageFormat<T>(GrainStateRecord entity)
        {
            T? dataValue = default;
            try
            {
                if (entity.State is { Length: > 0 })
                    dataValue = this.options.GrainStorageSerializer.Deserialize<T>(entity.State);
            }
            catch (Exception exc)
            {
                var sb = new StringBuilder();
                sb.AppendFormat(CultureInfo.CurrentCulture, "Unable to convert from storage format GrainStateEntity.Data={0}", entity.State);

                if (dataValue != null)
                {
                    sb.AppendFormat(CultureInfo.CurrentCulture, "Data Value={0} Type={1}", dataValue, dataValue.GetType());
                }

                var message = sb.ToString();
                LogError(logger, message);
                throw new AggregateException(message, exc);
            }

            return dataValue;
        }

        internal void ConvertToStorageFormat(object? grainState, GrainStateRecord entity)
        {
            int dataSize;
            // Convert to binary format
            entity.State = this.options.GrainStorageSerializer.Serialize(grainState).ToArray();
            dataSize = BINARY_STATE_PROPERTY_NAME.Length + entity.State.Length;

            LogTraceWritingBinaryData(logger, dataSize, entity.GrainReference, entity.GrainType);

            var pkSize = GRAIN_REFERENCE_PROPERTY_NAME.Length + entity.GrainReference.Length;
            var rkSize = GRAIN_TYPE_PROPERTY_NAME.Length + entity.GrainType.Length;
            var versionSize = ETAG_PROPERTY_NAME.Length + entity.ETag.ToString(CultureInfo.InvariantCulture).Length;

            if ((pkSize + rkSize + versionSize + dataSize) > MAX_DATA_SIZE)
            {
                var msg = $"Data too large to write to DynamoDB table. Size={dataSize} MaxSize={MAX_DATA_SIZE}";
                throw new ArgumentOutOfRangeException(nameof(grainState), msg);
            }
        }

        private void ResetGrainState<T>(IGrainState<T> grainState)
        {
            grainState.RecordExists = false;
            grainState.ETag = null;
            grainState.State = CreateInstance<T>();
        }

        private T CreateInstance<T>() => _activatorProvider.GetActivator<T>().Create();

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "AWS DynamoDB Grain Storage {Name} is initializing: {InitMsg}"
        )]
        private static partial void LogInformationInitializingDynamoDBGrainStorage(ILogger logger, string name, string initMsg);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Initializing provider {Name} of type {Type} in stage {Stage} took {ElapsedMilliseconds} Milliseconds."
        )]
        private static partial void LogInformationProviderInitialized(ILogger logger, string name, string type, int stage, long elapsedMilliseconds);

        [LoggerMessage(
            EventId = (int)ErrorCode.Provider_ErrorFromInit,
            Level = LogLevel.Error,
            Message = "Initialization failed for provider {Name} of type {Type} in stage {Stage} in {ElapsedMilliseconds} Milliseconds."
        )]
        private static partial void LogErrorProviderInitFailed(ILogger logger, string name, string type, int stage, long elapsedMilliseconds, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Reading: GrainType={GrainType} Pk={PartitionKey} GrainId={GrainId} from Table={TableName}"
        )]
        private static partial void LogTraceReadingGrainState(ILogger logger, string grainType, string partitionKey, GrainId grainId, string tableName);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error Writing: GrainType={GrainType} GrainId={GrainId} ETag={ETag} to Table={TableName}"
        )]
        private static partial void LogErrorWritingGrainState(ILogger logger, Exception exception, string grainType, GrainId grainId, string? eTag, string tableName);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Clearing: GrainType={GrainType} Pk={PartitionKey} GrainId={GrainId} ETag={ETag} DeleteStateOnClear={DeleteStateOnClear} from Table={TableName}"
        )]
        private static partial void LogTraceClearingGrainState(ILogger logger, string grainType, string partitionKey, GrainId grainId, string? eTag, bool deleteStateOnClear, string tableName);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error {Operation}: GrainType={GrainType} GrainId={GrainId} ETag={ETag} from Table={TableName}"
        )]
        private static partial void LogErrorClearingGrainState(ILogger logger, Exception exception, string operation, string grainType, GrainId grainId, string? eTag, string tableName);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "{Message}"
        )]
        private static partial void LogError(ILogger logger, string message);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Writing binary data size = {DataSize} for grain id = Partition={Partition} / Row={Row}"
        )]
        private static partial void LogTraceWritingBinaryData(ILogger logger, int dataSize, string partition, string row);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "DynamoDB Grain Storage {Name} has no ServiceId, so the keys in {TableName} start with an underscore rather than ClusterOptions.ServiceId as in the other providers. Set ServiceId, or set UseClusterServiceId to use ClusterOptions.ServiceId, with MigrateLegacyKeys to move the existing state. A future major version uses ClusterOptions.ServiceId by default."
        )]
        private static partial void LogWarningEmptyServiceId(ILogger logger, string name, string tableName);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Read the state of GrainType={GrainType} GrainId={GrainId} from its key without ServiceId in {TableName}; it is moved on the next write."
        )]
        private static partial void LogWarningReadingLegacyKey(ILogger logger, string grainType, GrainId grainId, string tableName);
    }

    /// <summary>
    /// Creates configured <see cref="DynamoDBGrainStorage"/> instances.
    /// </summary>
    public static class DynamoDBGrainStorageFactory
    {
        /// <summary>
        /// Creates a DynamoDB grain storage provider with the specified name.
        /// </summary>
        /// <param name="services">The service provider.</param>
        /// <param name="name">The storage provider name.</param>
        /// <returns>The configured grain storage provider.</returns>
        public static DynamoDBGrainStorage Create(IServiceProvider services, string name)
        {
            var optionsMonitor = services.GetRequiredService<IOptionsMonitor<DynamoDBStorageOptions>>();
            return ActivatorUtilities.CreateInstance<DynamoDBGrainStorage>(services, optionsMonitor.Get(name), name);
        }
    }
}
