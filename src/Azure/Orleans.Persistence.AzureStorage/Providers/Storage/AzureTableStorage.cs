using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Persistence.AzureStorage;
using Orleans.Providers.Azure;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Orleans.Storage
{
    /// <summary>
    /// Simple storage for writing grain state data to Azure table storage.
    /// </summary>
    public class AzureTableGrainStorage : IGrainStorage, IRestExceptionDecoder, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly AzureTableStorageOptions options;
        private readonly ClusterOptions clusterOptions;
        private readonly IGrainStorageSerializer storageSerializer;
        private readonly ILogger logger;

        private GrainStateTableDataManager tableDataManager;

        // each property can hold 64KB of data and each entity can take 1MB in total, so 15 full properties take
        // 15 * 64 = 960 KB leaving room for the primary key, timestamp etc
        private const int MAX_DATA_CHUNK_SIZE = 64 * 1024;
        private const int MAX_STRING_PROPERTY_LENGTH = 32 * 1024;
        private const int MAX_DATA_CHUNKS_COUNT = 15;

        private const string BINARY_DATA_PROPERTY_NAME = "Data";
        private const string STRING_DATA_PROPERTY_NAME = "StringData";
        private string name;

        /// <summary> Default constructor </summary>
        public AzureTableGrainStorage(
            string name,
            AzureTableStorageOptions options,
            IOptions<ClusterOptions> clusterOptions,
            IServiceProvider services,
            ILogger<AzureTableGrainStorage> logger)
        {
            this.options = options;
            this.clusterOptions = clusterOptions.Value;
            this.name = name;
            this.storageSerializer = options.GrainStorageSerializer;
            this.logger = logger;
        }

        /// <summary> Read state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.ReadStateAsync{T}"/>
        public async Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            if (tableDataManager == null) throw new ArgumentException("GrainState-Table property not initialized");

            string pk = GetKeyString(grainId);
            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace((int)AzureProviderErrorCode.AzureTableProvider_ReadingData,
                "Reading: GrainType={GrainType} Pk={PartitionKey} Grainid={GrainId} from Table={TableName}",
                grainType,
                pk,
                grainId,
                this.options.TableName);
            string partitionKey = pk;
            string rowKey = AzureTableUtils.SanitizeTableProperty(grainType);
            var entity = await tableDataManager.Read(partitionKey, rowKey).ConfigureAwait(false);
            if (entity is not null)
            {
                var loadedState = ConvertFromStorageFormat<T>(entity);
                grainState.RecordExists = loadedState != null;
                grainState.State = loadedState ?? Activator.CreateInstance<T>();
                grainState.ETag = entity.ETag.ToString();
            }
            // Else leave grainState in previous default condition
        }

        /// <summary> Write state data function for this storage provider. </summary>
        /// <see cref="IGrainStorage.WriteStateAsync{T}"/>
        public async Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            if (tableDataManager == null) throw new ArgumentException("GrainState-Table property not initialized");

            string pk = GetKeyString(grainId);
            if (logger.IsEnabled(LogLevel.Trace))
                logger.LogTrace((int)AzureProviderErrorCode.AzureTableProvider_WritingData,
                    "Writing: GrainType={GrainType} Pk={PartitionKey} Grainid={GrainId} ETag={ETag} to Table={TableName}",
                    grainType,
                    pk,
                    grainId,
                    grainState.ETag,
                    this.options.TableName);

            var rowKey = AzureTableUtils.SanitizeTableProperty(grainType);
            var entity = new TableEntity(pk, rowKey)
            {
                ETag = new ETag(grainState.ETag)
            };
            ConvertToStorageFormat(grainState.State, entity);
            try
            {
                await DoOptimisticUpdate(() => tableDataManager.Write(entity), grainType, grainId, this.options.TableName, grainState.ETag).ConfigureAwait(false);
                grainState.ETag = entity.ETag.ToString();
                grainState.RecordExists = true;
            }
            catch (Exception exc)
            {
                logger.LogError((int)AzureProviderErrorCode.AzureTableProvider_WriteError, exc,
                    "Error Writing: GrainType={GrainType} GrainId={GrainId} ETag={ETag} to Table={TableName}",
                    grainType,
                    grainId,
                    grainState.ETag,
                    this.options.TableName);
                throw;
            }
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
            if (tableDataManager == null) throw new ArgumentException("GrainState-Table property not initialized");

            string pk = GetKeyString(grainId);
            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace((int)AzureProviderErrorCode.AzureTableProvider_WritingData,
                "Clearing: GrainType={GrainType} Pk={PartitionKey} Grainid={GrainId} ETag={ETag} DeleteStateOnClear={DeleteStateOnClear} from Table={TableName}",
                grainType,
                pk,
                grainId,
                grainState.ETag,
                this.options.DeleteStateOnClear,
                this.options.TableName);
            var rowKey = AzureTableUtils.SanitizeTableProperty(grainType);
            var entity = new TableEntity(pk, rowKey)
            {
                ETag = new ETag(grainState.ETag)
            };
            string operation = "Clearing";
            try
            {
                if (this.options.DeleteStateOnClear)
                {
                    operation = "Deleting";
                    await DoOptimisticUpdate(() => tableDataManager.Delete(entity), grainType, grainId, this.options.TableName, grainState.ETag).ConfigureAwait(false);
                }
                else
                {
                    await DoOptimisticUpdate(() => tableDataManager.Write(entity), grainType, grainId, this.options.TableName, grainState.ETag).ConfigureAwait(false);
                }

                grainState.ETag = entity.ETag.ToString(); // Update in-memory data to the new ETag
                grainState.RecordExists = false;
            }
            catch (Exception exc)
            {
                logger.LogError((int)AzureProviderErrorCode.AzureTableProvider_DeleteError,
                    exc,
                    "Error {Operation}: GrainType={GrainType} Grainid={GrainId} ETag={ETag} from Table={TableName}",
                    operation,
                    grainType,
                    grainId,
                    grainState.ETag,
                    this.options.TableName);
                throw;
            }
        }

        private static async Task DoOptimisticUpdate(Func<Task> updateOperation, string grainType, GrainId grainId, string tableName, string currentETag)
        {
            try
            {
                await updateOperation.Invoke().ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (ex.IsPreconditionFailed() || ex.IsConflict() || ex.IsNotFound())
            {
                throw new TableStorageUpdateConditionNotSatisfiedException(grainType, grainId.ToString(), tableName, "Unknown", currentETag, ex);
            }
        }

        /// <summary>
        /// Serialize to Azure storage format in either binary or JSON format.
        /// </summary>
        /// <param name="grainState">The grain state data to be serialized</param>
        /// <param name="entity">The Azure table entity the data should be stored in</param>
        /// <remarks>
        /// See:
        /// http://msdn.microsoft.com/en-us/library/system.web.script.serialization.javascriptserializer.aspx
        /// for more on the JSON serializer.
        /// </remarks>
        internal void ConvertToStorageFormat<T>(T grainState, TableEntity entity)
        {
            int dataSize;
            IEnumerable<ReadOnlyMemory<byte>> properties;
            string basePropertyName;

            // Convert to binary format
            var data = this.storageSerializer.Serialize<T>(grainState);
            basePropertyName = BINARY_DATA_PROPERTY_NAME;

            dataSize = data.ToMemory().Length;
            properties = SplitBinaryData(data);

            CheckMaxDataSize(dataSize, MAX_DATA_CHUNK_SIZE * MAX_DATA_CHUNKS_COUNT);

            foreach (var keyValuePair in properties.Zip(GetPropertyNames(basePropertyName),
                (property, name) => new KeyValuePair<string, object>(name, property.ToArray())))
            {
                entity[keyValuePair.Key] = keyValuePair.Value;
            }
        }

        private void CheckMaxDataSize(int dataSize, int maxDataSize)
        {
            if (dataSize > maxDataSize)
            {
                var msg = string.Format("Data too large to write to Azure table. Size={0} MaxSize={1}", dataSize, maxDataSize);
                logger.LogError(0, "Data too large to write to Azure table. Size={Size} MaxSize={MaxSize}", dataSize, maxDataSize);
                throw new ArgumentOutOfRangeException("GrainState.Size", msg);
            }
        }

        private static IEnumerable<ReadOnlyMemory<char>> SplitStringData(ReadOnlyMemory<char> stringData)
        {
            var startIndex = 0;
            while (startIndex < stringData.Length)
            {
                var chunkSize = Math.Min(MAX_STRING_PROPERTY_LENGTH, stringData.Length - startIndex);

                yield return stringData.Slice(startIndex, chunkSize);

                startIndex += chunkSize;
            }
        }

        private static IEnumerable<ReadOnlyMemory<byte>> SplitBinaryData(ReadOnlyMemory<byte> binaryData)
        {
            var startIndex = 0;
            while (startIndex < binaryData.Length)
            {
                var chunkSize = Math.Min(MAX_DATA_CHUNK_SIZE, binaryData.Length - startIndex);

                yield return binaryData.Slice(startIndex, chunkSize);

                startIndex += chunkSize;
            }
        }

        private static IEnumerable<string> GetPropertyNames(string basePropertyName)
        {
            yield return basePropertyName;
            for (var i = 1; i < MAX_DATA_CHUNKS_COUNT; ++i)
            {
                yield return basePropertyName + i;
            }
        }

        private static IEnumerable<byte[]> ReadBinaryDataChunks(TableEntity entity)
        {
            foreach (var binaryDataPropertyName in GetPropertyNames(BINARY_DATA_PROPERTY_NAME))
            {
                if (entity.TryGetValue(binaryDataPropertyName, out var dataProperty))
                {
                    switch (dataProperty)
                    {
                        // if TablePayloadFormat.JsonNoMetadata is used
                        case string stringValue:
                            if (!string.IsNullOrEmpty(stringValue))
                            {
                                yield return Convert.FromBase64String(stringValue);
                            }
                            break;

                        // if any payload type providing metadata is used
                        case byte[] binaryValue:
                            if (binaryValue != null && binaryValue.Length > 0)
                            {
                                yield return binaryValue;
                            }
                            break;
                    }
                }
            }
        }

        private static byte[] ReadBinaryData(TableEntity entity)
        {
            var dataChunks = ReadBinaryDataChunks(entity).ToArray();
            var dataSize = dataChunks.Select(d => d.Length).Sum();
            var result = new byte[dataSize];
            var startIndex = 0;
            foreach (var dataChunk in dataChunks)
            {
                Array.Copy(dataChunk, 0, result, startIndex, dataChunk.Length);
                startIndex += dataChunk.Length;
            }
            return result;
        }

        private static IEnumerable<string> ReadStringDataChunks(TableEntity entity)
        {
            foreach (var stringDataPropertyName in GetPropertyNames(STRING_DATA_PROPERTY_NAME))
            {
                if (entity.TryGetValue(stringDataPropertyName, out var dataProperty))
                {
                    if (dataProperty is string {Length: > 0 } data)
                    {
                        yield return data;
                    }
                }
            }
        }

        private static string ReadStringData(TableEntity entity)
        {
            return string.Join(string.Empty, ReadStringDataChunks(entity));
        }

        /// <summary>
        /// Deserialize from Azure storage format
        /// </summary>
        /// <param name="entity">The Azure table entity the stored data</param>
        internal T ConvertFromStorageFormat<T>(TableEntity entity)
        {
            // Read from both column type for backward compatibility
            var binaryData = ReadBinaryData(entity);
            var stringData = ReadStringData(entity);

            T dataValue = default;
            try
            {
                var input = binaryData.Length > 0
                    ? new BinaryData(binaryData)
                    : new BinaryData(stringData);
                dataValue = this.storageSerializer.Deserialize<T>(input);
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

                logger.LogError(exc, "{Message}", sb.ToString());
                throw new AggregateException(sb.ToString(), exc);
            }

            return dataValue;
        }

        private string GetKeyString(GrainId grainId)
        {
            var key = $"{clusterOptions.ServiceId}_{grainId}";
            return AzureTableUtils.SanitizeTableProperty(key);
        }

        private class GrainStateTableDataManager
        {
            public string TableName { get; private set; }
            private readonly AzureTableDataManager<TableEntity> tableManager;
            private readonly ILogger logger;

            public GrainStateTableDataManager(AzureStorageOperationOptions options, ILogger logger)
            {
                this.logger = logger;
                TableName = options.TableName;
                tableManager = new AzureTableDataManager<TableEntity>(options, logger);
            }

            public Task InitTableAsync()
            {
                return tableManager.InitTableAsync();
            }

            public async Task<TableEntity> Read(string partitionKey, string rowKey)
            {
                if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace((int)AzureProviderErrorCode.AzureTableProvider_Storage_Reading,
                    "Reading: PartitionKey={PartitionKey} RowKey={RowKey} from Table={TableName}",
                    partitionKey,
                    rowKey,
                    TableName);
                try
                {
                    var data = await tableManager.ReadSingleTableEntryAsync(partitionKey, rowKey).ConfigureAwait(false);
                    if (data.Entity == null)
                    {
                        if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace((int)AzureProviderErrorCode.AzureTableProvider_DataNotFound,
                            "DataNotFound reading: PartitionKey={PartitionKey} RowKey={RowKey} from Table={TableName}",
                            partitionKey,
                            rowKey,
                            TableName);
                        return default;
                    }
                    TableEntity stateEntity = data.Entity;
                    var record = stateEntity;
                    record.ETag = new ETag(data.ETag);
                    if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace((int)AzureProviderErrorCode.AzureTableProvider_Storage_DataRead,
                        "Read: PartitionKey={PartitionKey} RowKey={RowKey} from Table={TableName} with ETag={ETag}",
                        stateEntity.PartitionKey,
                        stateEntity.RowKey,
                        TableName,
                        record.ETag);

                    return record;
                }
                catch (Exception exc)
                {
                    if (AzureTableUtils.TableStorageDataNotFound(exc))
                    {
                        if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace((int)AzureProviderErrorCode.AzureTableProvider_DataNotFound,
                            exc,
                            "DataNotFound reading (exception): PartitionKey={PartitionKey} RowKey={RowKey} from Table={TableName}",
                            partitionKey,
                            rowKey,
                            TableName);

                        return default;  // No data
                    }
                    throw;
                }
            }

            public async Task Write(TableEntity entity)
            {
                if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace((int)AzureProviderErrorCode.AzureTableProvider_Storage_Writing,
                    "Writing: PartitionKey={PartitionKey} RowKey={RowKey} to Table={TableName} with ETag={ETag}",
                    entity.PartitionKey,
                    entity.RowKey,
                    TableName,
                    entity.ETag);

                string eTag = string.IsNullOrEmpty(entity.ETag.ToString()) ?
                    await tableManager.CreateTableEntryAsync(entity).ConfigureAwait(false) :
                    await tableManager.UpdateTableEntryAsync(entity, entity.ETag).ConfigureAwait(false);
                entity.ETag = new ETag(eTag);
            }

            public async Task Delete(TableEntity entity)
            {
                if (string.IsNullOrWhiteSpace(entity.ETag.ToString()))
                {
                    if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace((int)AzureProviderErrorCode.AzureTableProvider_DataNotFound,
                        "Not attempting to delete non-existent persistent state: PartitionKey={PartitionKey} RowKey={RowKey} from Table={TableName} with ETag={ETag}",
                        entity.PartitionKey,
                        entity.RowKey,
                        TableName,
                        entity.ETag);
                    return;
                }

                if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace((int)AzureProviderErrorCode.AzureTableProvider_Storage_Writing,
                    "Deleting: PartitionKey={PartitionKey} RowKey={RowKey} from Table={TableName} with ETag={ETag}",
                    entity.PartitionKey,
                    entity.RowKey,
                    TableName,
                    entity.ETag);
                await tableManager.DeleteTableEntryAsync(entity, entity.ETag).ConfigureAwait(false);
                entity.ETag = default;
            }
        }

        /// <summary> Decodes Storage exceptions.</summary>
        public bool DecodeException(Exception e, out HttpStatusCode httpStatusCode, out string restStatus, bool getRESTErrors = false)
        {
            return AzureTableUtils.EvaluateException(e, out httpStatusCode, out restStatus, getRESTErrors);
        }

        private async Task Init(CancellationToken ct)
        {
            var stopWatch = Stopwatch.StartNew();
            try
            {
                this.logger.LogInformation((int)AzureProviderErrorCode.AzureTableProvider_InitProvider,
                    "AzureTableGrainStorage {ProviderName} initializing: {Options}",
                    name,
                    this.options.ToString());
                this.tableDataManager = new GrainStateTableDataManager(this.options, this.logger);
                await this.tableDataManager.InitTableAsync();
                stopWatch.Stop();
                this.logger.LogInformation((int)AzureProviderErrorCode.AzureTableProvider_InitProvider,
                    "Initializing provider {ProviderName} of type {ProviderType} in stage {Stage} took {ElapsedMilliseconds} Milliseconds.",
                    this.name,
                    this.GetType().Name,
                    this.options.InitStage,
                    stopWatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopWatch.Stop();
                this.logger.LogError((int)ErrorCode.Provider_ErrorFromInit, ex,
                    "Initialization failed for provider {ProviderName} of type {ProviderType} in stage {Stage} in {ElapsedMilliseconds} Milliseconds.",
                    this.name,
                    this.GetType().Name,
                    this.options.InitStage,
                    stopWatch.ElapsedMilliseconds);
                throw;
            }
        }

        private Task Close(CancellationToken ct)
        {
            this.tableDataManager = null;
            return Task.CompletedTask;
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(OptionFormattingUtilities.Name<AzureTableGrainStorage>(this.name), this.options.InitStage, Init, Close);
        }
    }

    public static class AzureTableGrainStorageFactory
    {
        public static IGrainStorage Create(IServiceProvider services, string name)
        {
            var optionsSnapshot = services.GetRequiredService<IOptionsMonitor<AzureTableStorageOptions>>();
            var clusterOptions = services.GetProviderClusterOptions(name);
            return ActivatorUtilities.CreateInstance<AzureTableGrainStorage>(services, name, optionsSnapshot.Get(name), clusterOptions);
        }
    }
}
