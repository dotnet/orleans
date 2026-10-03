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

        /// <summary>Runs between reading and writing the key format record, so that tests can interleave another silo.</summary>
        internal Func<Task>? BeforeKeyFormatWriteForTesting { get; set; }

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
                var keyFormat = await ResolveKeyFormatAsync(ct);
                LogInformationKeyFormat(logger, this.name, this.options.TableName, _keyServiceId, keyFormat);
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
                else if (_migrateLegacyKeys && !string.IsNullOrWhiteSpace(grainState.ETag))
                {
                    await WriteAndRetireLegacyAsync(grainState, record, GetLegacyKeyString(grainId), clear: false);
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
            if (TtlAttribute() is { } ttl)
            {
                fields.Add(GRAIN_TTL_PROPERTY_NAME, ttl);
            }

            fields.Add(BINARY_STATE_PROPERTY_NAME, StateAttribute(record.State));

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

                    if (_migrateLegacyKeys && !string.IsNullOrWhiteSpace(grainState.ETag))
                    {
                        // a legacy item left beside the current one, by a write made before the migration was enabled,
                        // would come back through the read fallback once the current item is gone: it goes with it.
                        // A state never read has no ETag, and its clear leaves whatever is there, as before.
                        await this.storage.WriteTxAsync(deletes:
                        [
                            new Delete { TableName = this.options.TableName, Key = keys, ConditionExpression = expression, ExpressionAttributeValues = conditionalValues },
                            new Delete { TableName = this.options.TableName, Key = Keys(GetLegacyKeyString(grainId), record.GrainType) },
                        ]);
                    }
                    else
                    {
                        await this.storage.DeleteEntryAsync(this.options.TableName, keys, expression, conditionalValues).ConfigureAwait(false);
                    }

                    ResetGrainState(grainState);
                }
                else
                {
                    if (_migrateLegacyKeys && !string.IsNullOrWhiteSpace(grainState.ETag))
                    {
                        await WriteAndRetireLegacyAsync(grainState, record, GetLegacyKeyString(grainId), clear: true);
                    }
                    else
                    {
                        await WriteStateInternal(grainState, record, true);
                    }

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
                Keys(partitionKey, rowKey),
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
        /// it is. An empty one means <see cref="ClusterOptions.ServiceId"/>, unless the options keep the empty one; state
        /// written with the empty one, recorded in the table or found in it, stops initialization until the options say
        /// whether to keep or migrate it. The choice is recorded in the table.
        /// </summary>
        private async Task<string> ResolveKeyFormatAsync(CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(this.options.ServiceId))
            {
                _keyServiceId = this.options.ServiceId;
                if (this.options.UseClusterServiceId is not null || this.options.MigrateLegacyKeys is not null)
                {
                    LogWarningKeyFormatOptionsIgnored(logger, this.name);
                }

                return nameof(DynamoDBStorageOptions.ServiceId);
            }

            // Silos starting together may choose differently: the record is written only if it still holds what was read,
            // and a silo that loses decides again from what the other one wrote.
            string format;
            while (true)
            {
                var recordedFormat = await ReadKeyFormatAsync(ct);
                if (recordedFormat is not (null or LEGACY_KEY_FORMAT or CLUSTER_KEY_FORMAT or MIGRATING_KEY_FORMAT))
                {
                    // a format this version does not know, from a newer one: following it wrongly would split the state
                    throw new OrleansConfigurationException(
                        $"DynamoDB Grain Storage {this.name} cannot start: table {this.options.TableName} records "
                        + (recordedFormat.Length == 0 ? "an empty key format" : $"the key format '{recordedFormat}'")
                        + ", which this version does not know. Run a version that supports it.");
                }

                // Going back to the empty ServiceId would hide the state written since under ClusterOptions.ServiceId, and
                // every silo that follows the record would go back with this one.
                if (this.options.UseClusterServiceId == false && recordedFormat is CLUSTER_KEY_FORMAT or MIGRATING_KEY_FORMAT)
                {
                    throw new OrleansConfigurationException(
                        $"DynamoDB Grain Storage {this.name} cannot start: {nameof(DynamoDBStorageOptions.UseClusterServiceId)} is false, "
                        + $"but table {this.options.TableName} records that its keys are built from ClusterOptions.ServiceId ('{recordedFormat}'), "
                        + $"so the state written since would not be found. Set {nameof(DynamoDBStorageOptions.UseClusterServiceId)} to true or "
                        + $"leave it unset. To go back to an empty ServiceId on purpose, delete the {KEY_FORMAT_MARKER} item first.");
                }

                // Grains written under ClusterOptions.ServiceId without a migration leave their legacy item behind, and the
                // read fallback would bring it back for those whose current item has been deleted or has expired.
                if (this.options.MigrateLegacyKeys == true && recordedFormat == CLUSTER_KEY_FORMAT)
                {
                    throw new OrleansConfigurationException(
                        $"DynamoDB Grain Storage {this.name} cannot start: {nameof(DynamoDBStorageOptions.MigrateLegacyKeys)} is true, "
                        + $"but table {this.options.TableName} records that its keys have been built from ClusterOptions.ServiceId without "
                        + "a migration, so a grain whose state there has since been deleted or has expired would read back its older state "
                        + $"from the key without ServiceId. Set {nameof(DynamoDBStorageOptions.MigrateLegacyKeys)} to false or leave it unset. "
                        + $"To migrate anyway, when no such state has been deleted or has expired, delete the {KEY_FORMAT_MARKER} item first.");
                }

                // Silos that chose the empty ServiceId may still be writing its keys, so a table that records it does not
                // switch by itself, whether or not a scan would find their state yet.
                if (this.options.UseClusterServiceId is null && recordedFormat == LEGACY_KEY_FORMAT)
                {
                    throw new OrleansConfigurationException(
                        $"DynamoDB Grain Storage {this.name} has no ServiceId, and table {this.options.TableName} records that its keys "
                        + "are built with an empty ServiceId, which silos running an earlier version may still be writing. "
                        + $"{nameof(DynamoDBStorageOptions.ServiceId)} now defaults to ClusterOptions.ServiceId. Set "
                        + $"{nameof(DynamoDBStorageOptions.UseClusterServiceId)} to false to keep the empty ServiceId, or to true, once "
                        + $"every silo runs this version, with {nameof(DynamoDBStorageOptions.MigrateLegacyKeys)} to move the state to "
                        + "ClusterOptions.ServiceId.");
                }

                if (this.options.UseClusterServiceId is null
                    && recordedFormat is null
                    && await HasLegacyStateAsync(ct))
                {
                    throw new OrleansConfigurationException(
                        $"DynamoDB Grain Storage {this.name} has no ServiceId, and table {this.options.TableName} holds state written "
                        + $"with an empty ServiceId, whose keys start with an underscore. {nameof(DynamoDBStorageOptions.ServiceId)} now "
                        + $"defaults to ClusterOptions.ServiceId. Set {nameof(DynamoDBStorageOptions.UseClusterServiceId)} to false to keep "
                        + $"using that state as it is, or to true with {nameof(DynamoDBStorageOptions.MigrateLegacyKeys)} to move it to "
                        + "ClusterOptions.ServiceId.");
                }

                var useClusterServiceId = this.options.UseClusterServiceId ?? true;

                _keyServiceId = useClusterServiceId ? this.options.ClusterServiceId : string.Empty;
                _migrateLegacyKeys = useClusterServiceId && (this.options.MigrateLegacyKeys ?? recordedFormat == MIGRATING_KEY_FORMAT);

                format = !useClusterServiceId ? LEGACY_KEY_FORMAT
                    : _migrateLegacyKeys ? MIGRATING_KEY_FORMAT
                    : CLUSTER_KEY_FORMAT;
                if (recordedFormat == format || await TryWriteKeyFormatAsync(recordedFormat, format, ct))
                {
                    break;
                }
            }

            if (string.IsNullOrEmpty(_keyServiceId))
            {
                LogWarningEmptyServiceId(logger, this.name, this.options.TableName);
                if (this.options.MigrateLegacyKeys == true)
                {
                    LogWarningMigrationIgnored(logger, this.name, this.options.TableName);
                }
            }

            return format;
        }

        private async Task<bool> TryWriteKeyFormatAsync(string? recordedFormat, string format, CancellationToken ct)
        {
            if (BeforeKeyFormatWriteForTesting is { } beforeWrite)
            {
                await beforeWrite();
            }

            try
            {
                var fields = Keys(KEY_FORMAT_MARKER, KEY_FORMAT_MARKER);
                fields.Add(KEY_FORMAT_PROPERTY_NAME, new AttributeValue(format));
                await this.storage.PutEntryAsync(this.options.TableName, fields, ct,
                recordedFormat is null ? $"attribute_not_exists({GRAIN_REFERENCE_PROPERTY_NAME})" : $"{KEY_FORMAT_PROPERTY_NAME} = :recordedFormat",
                recordedFormat is null ? null : new Dictionary<string, AttributeValue> { { ":recordedFormat", new AttributeValue(recordedFormat) } });
                return true;
            }
            catch (ConditionalCheckFailedException)
            {
                return false;
            }
        }

        /// <summary>
        /// Whether the table holds state written with an empty <see cref="DynamoDBStorageOptions.ServiceId"/>, whose keys
        /// start with an underscore, as the key format record does. State written with any other ServiceId is not it, but
        /// one starting with an underscore cannot be told apart, and the docs say to set
        /// <see cref="DynamoDBStorageOptions.UseClusterServiceId"/> explicitly then.
        /// </summary>
        private Task<bool> HasLegacyStateAsync(CancellationToken ct) =>
            this.storage.AnyAsync(this.options.TableName,
                $"begins_with({GRAIN_REFERENCE_PROPERTY_NAME}, :legacyPrefix) AND {GRAIN_REFERENCE_PROPERTY_NAME} <> :keyFormatMarker",
                new Dictionary<string, AttributeValue>
                {
                    { ":legacyPrefix", new AttributeValue("_") },
                    { ":keyFormatMarker", new AttributeValue(KEY_FORMAT_MARKER) }
                },
                ct);

        private async Task<string?> ReadKeyFormatAsync(CancellationToken ct)
        {
            var marker = await this.storage.ReadSingleEntryAsync(this.options.TableName,
                Keys(KEY_FORMAT_MARKER, KEY_FORMAT_MARKER),
                fields => fields.TryGetValue(KEY_FORMAT_PROPERTY_NAME, out var value) ? value.S ?? string.Empty : string.Empty,
                ct);

            // null is no record; a record without a value is one this version cannot follow, and is refused as an unknown one
            return marker;
        }

        /// <summary>
        /// Writes the state of a grain read from its legacy key under its current key, and deletes the legacy record in the
        /// same transaction, so that the grain never has both or neither.
        /// </summary>
        private async Task MigrateStateAsync<T>(IGrainState<T> grainState, GrainStateRecord record, string legacyPartitionKey, int legacyETag, bool clear)
        {
            var newETag = legacyETag + 1;
            var fields = Keys(record.GrainReference, record.GrainType);
            fields.Add(ETAG_PROPERTY_NAME, new AttributeValue { N = newETag.ToString(CultureInfo.InvariantCulture) });
            fields.Add(BINARY_STATE_PROPERTY_NAME, StateAttribute(record.State));
            if (TtlAttribute() is { } ttl)
            {
                fields.Add(GRAIN_TTL_PROPERTY_NAME, ttl);
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

        /// <summary>
        /// Writes or clears a state read from the current key, and deletes the grain's legacy item in the same transaction.
        /// An item written while the migration was off leaves its legacy item behind, which the read fallback would bring
        /// back once the current item is gone, through <see cref="DynamoDBStorageOptions.TimeToLive"/> or a delete.
        /// </summary>
        private async Task WriteAndRetireLegacyAsync<T>(IGrainState<T> grainState, GrainStateRecord record, string legacyPartitionKey, bool clear)
        {
            // no item has an ETag that is not a number, so it cannot be the current state; taken as 0, it would pass the
            // condition on an item written once
            if (!int.TryParse(grainState.ETag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var currentETag))
            {
                throw new InconsistentStateException($"Inconsistent grain state: the ETag '{grainState.ETag}' is not one this provider writes");
            }

            var newETag = currentETag + 1;
            var values = new Dictionary<string, AttributeValue>
            {
                { CURRENT_ETAG_ALIAS, new AttributeValue { N = currentETag.ToString(CultureInfo.InvariantCulture) } },
                { ":newETag", new AttributeValue { N = newETag.ToString(CultureInfo.InvariantCulture) } },
                { ":state", StateAttribute(record.State) },
            };
            var update = $"SET {ETAG_PROPERTY_NAME} = :newETag, {BINARY_STATE_PROPERTY_NAME} = :state";
            if (TtlAttribute() is { } ttl)
            {
                values.Add(":ttl", ttl);
                update += $", {GRAIN_TTL_PROPERTY_NAME} = :ttl";
            }

            await this.storage.WriteTxAsync(
                updates: [new Update
                {
                    TableName = this.options.TableName,
                    Key = Keys(record.GrainReference, record.GrainType),
                    UpdateExpression = update,
                    ConditionExpression = $"{ETAG_PROPERTY_NAME} = {CURRENT_ETAG_ALIAS}",
                    ExpressionAttributeValues = values,
                }],
                deletes: [new Delete { TableName = this.options.TableName, Key = Keys(legacyPartitionKey, record.GrainType) }]);

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
            var fields = await this.storage.ReadSingleEntryAsync(this.options.TableName, Keys(legacyPartitionKey, rowKey), fields => fields);

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
                    Key = Keys(partitionKey, rowKey),
                    ConditionExpression = $"attribute_not_exists({GRAIN_REFERENCE_PROPERTY_NAME})",
                }]);

        private Delete LegacyDelete(string legacyPartitionKey, string rowKey, int legacyETag) => new()
        {
            TableName = this.options.TableName,
            Key = Keys(legacyPartitionKey, rowKey),
            ConditionExpression = $"{ETAG_PROPERTY_NAME} = {CURRENT_ETAG_ALIAS}",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { { CURRENT_ETAG_ALIAS, new AttributeValue { N = legacyETag.ToString(CultureInfo.InvariantCulture) } } },
        };

        private static Dictionary<string, AttributeValue> Keys(string partitionKey, string rowKey) => new()
        {
            { GRAIN_REFERENCE_PROPERTY_NAME, new AttributeValue(partitionKey) },
            { GRAIN_TYPE_PROPERTY_NAME, new AttributeValue(rowKey) }
        };

        private static AttributeValue StateAttribute(byte[]? state) =>
            state is { Length: > 0 } ? new AttributeValue { B = new MemoryStream(state) } : new AttributeValue { NULL = true };

        private AttributeValue? TtlAttribute() => this.options.TimeToLive is { } timeToLive
            ? new AttributeValue { N = ((DateTimeOffset)DateTime.UtcNow.Add(timeToLive)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) }
            : null;

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
            Message = "DynamoDB Grain Storage {Name} keeps an empty ServiceId, as UseClusterServiceId is false, so the keys in {TableName} start with an underscore rather than ClusterOptions.ServiceId as in the other providers. Set UseClusterServiceId to true, with MigrateLegacyKeys to move the existing state."
        )]
        private static partial void LogWarningEmptyServiceId(ILogger logger, string name, string tableName);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Read the state of GrainType={GrainType} GrainId={GrainId} from its key without ServiceId in {TableName}; it is moved on the next write."
        )]
        private static partial void LogWarningReadingLegacyKey(ILogger logger, string grainType, GrainId grainId, string tableName);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "DynamoDB Grain Storage {Name} builds the keys in {TableName} from the ServiceId '{KeyServiceId}' (key format {KeyFormat})."
        )]
        private static partial void LogInformationKeyFormat(ILogger logger, string name, string tableName, string keyServiceId, string keyFormat);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "DynamoDB Grain Storage {Name} has a ServiceId, so UseClusterServiceId and MigrateLegacyKeys have no effect: state written with an empty ServiceId is neither read nor moved."
        )]
        private static partial void LogWarningKeyFormatOptionsIgnored(ILogger logger, string name);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "DynamoDB Grain Storage {Name} keeps an empty ServiceId in {TableName}, so MigrateLegacyKeys has no effect. Set UseClusterServiceId to true to move the state to ClusterOptions.ServiceId."
        )]
        private static partial void LogWarningMigrationIgnored(ILogger logger, string name, string tableName);
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
