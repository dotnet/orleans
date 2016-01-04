using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans.AzureUtils;
using Orleans.Providers.Azure;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Providers;
using Orleans.Serialization;


namespace Orleans.Storage
{
    /// <summary>
    /// Simple storage provider for writing grain state data to Azure table storage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Required configuration params: <c>DataConnectionString</c>
    /// </para>
    /// <para>
    /// Optional configuration params: 
    /// <c>TableName</c> -- defaults to <c>OrleansGrainState</c>
    /// <c>DeleteStateOnClear</c> -- defaults to <c>false</c>
    /// </para>
    /// </remarks>
    /// <example>
    /// Example configuration for this storage provider in OrleansConfiguration.xml file:
    /// <code>
    /// &lt;OrleansConfiguration xmlns="urn:orleans">
    ///   &lt;Globals>
    ///     &lt;StorageProviders>
    ///       &lt;Provider Type="Orleans.Storage.AzureTableStorage" Name="AzureStore"
    ///         DataConnectionString="UseDevelopmentStorage=true"
    ///         DeleteStateOnClear="true"
    ///       />
    ///   &lt;/StorageProviders>
    /// </code>
    /// </example>
    public class AzureTableStorage : IStorageProvider, IRestExceptionDecoder
    {
        private const string DATA_CONNECTION_STRING = "DataConnectionString";
        private const string TABLE_NAME_PROPERTY = "TableName";
        private const string DELETE_ON_CLEAR_PROPERTY = "DeleteStateOnClear";
        private const string GRAIN_STATE_TABLE_NAME_DEFAULT = "OrleansGrainState";
        private string dataConnectionString;
        private string tableName;
        private string serviceId;
        private GrainStateTableDataManager tableDataManager;
        private bool isDeleteStateOnClear;
        private static int counter;
        private readonly int id;
        private const int MAX_DATA_SIZE = 64 * 1024; // 64KB
        private const string USE_JSON_FORMAT_PROPERTY = "UseJsonFormat";
        private bool useJsonFormat;
        private Newtonsoft.Json.JsonSerializerSettings jsonSettings;

        /// <summary> Name of this storage provider instance. </summary>
        /// <see cref="IProvider.Name"/>
        public string Name { get; private set; }

        /// <summary> Logger used by this storage provider instance. </summary>
        /// <see cref="IStorageProvider.Log"/>
        public Logger Log { get; private set; }

        /// <summary> Default constructor </summary>
        public AzureTableStorage()
        {
            tableName = GRAIN_STATE_TABLE_NAME_DEFAULT;
            id = Interlocked.Increment(ref counter);
        }

        /// <summary> Initialization function for this storage provider. </summary>
        /// <see cref="IProvider.Init"/>
        public Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            Name = name;
            serviceId = providerRuntime.ServiceId.ToString();

            if (!config.Properties.ContainsKey(DATA_CONNECTION_STRING) || string.IsNullOrWhiteSpace(config.Properties[DATA_CONNECTION_STRING]))
                throw new ArgumentException("DataConnectionString property not set");

            dataConnectionString = config.Properties["DataConnectionString"];

            if (config.Properties.ContainsKey(TABLE_NAME_PROPERTY))
                tableName = config.Properties[TABLE_NAME_PROPERTY];

            isDeleteStateOnClear = config.Properties.ContainsKey(DELETE_ON_CLEAR_PROPERTY) &&
                "true".Equals(config.Properties[DELETE_ON_CLEAR_PROPERTY], StringComparison.OrdinalIgnoreCase);

            Log = providerRuntime.GetLogger("Storage.AzureTableStorage." + id);

            var initMsg = string.Format("Init: Name={0} ServiceId={1} Table={2} DeleteStateOnClear={3}",
                Name, serviceId, tableName, isDeleteStateOnClear);

            if (config.Properties.ContainsKey(USE_JSON_FORMAT_PROPERTY))
                useJsonFormat = "true".Equals(config.Properties[USE_JSON_FORMAT_PROPERTY], StringComparison.OrdinalIgnoreCase);
            
            if (useJsonFormat)
            {
                jsonSettings = jsonSettings = OrleansJsonSerializer.SerializerSettings;
            }
            initMsg = String.Format("{0} UseJsonFormat={1}", initMsg, useJsonFormat);

            Log.Info((int)AzureProviderErrorCode.AzureTableProvider_InitProvider, initMsg);
            Log.Info((int)AzureProviderErrorCode.AzureTableProvider_ParamConnectionString, "AzureTableStorage Provider is using DataConnectionString: {0}", ConfigUtilities.PrintDataConnectionInfo(dataConnectionString));
            tableDataManager = new GrainStateTableDataManager(tableName, dataConnectionString, Log);
            return tableDataManager.InitTableAsync();
        }

        // Internal method to initialize for testing
        internal void InitLogger(Logger logger)
        {
            Log = logger;
        }

        /// <summary> Shutdown this storage provider. </summary>
        /// <see cref="IProvider.Close"/>
        public Task Close()
        {
            tableDataManager = null;
            return TaskDone.Done;
        }

        /// <summary> Read state data function for this storage provider. </summary>
        /// <see cref="IStorageProvider.ReadStateAsync"/>
        public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            if (tableDataManager == null) throw new ArgumentException("GrainState-Table property not initialized");

            string pk = GetKeyString(grainReference);
            if (Log.IsVerbose3) Log.Verbose3((int)AzureProviderErrorCode.AzureTableProvider_ReadingData, "Reading: GrainType={0} Pk={1} Grainid={2} from Table={3}", grainType, pk, grainReference, tableName);
            string partitionKey = pk;
            string rowKey = grainType;
            GrainStateRecord record = await tableDataManager.Read(partitionKey, rowKey);
            if (record != null)
            {
                var entity = record.Entity;
                if (entity != null)
                {
                    grainState.State = ConvertFromStorageFormat(entity);
                    grainState.ETag = record.ETag;
                }
            }

            // Else leave grainState in previous default condition
        }

        /// <summary> Write state data function for this storage provider. </summary>
        /// <see cref="IStorageProvider.WriteStateAsync"/>
        public async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            if (tableDataManager == null) throw new ArgumentException("GrainState-Table property not initialized");

            string pk = GetKeyString(grainReference);
            if (Log.IsVerbose3)
                Log.Verbose3((int)AzureProviderErrorCode.AzureTableProvider_WritingData, "Writing: GrainType={0} Pk={1} Grainid={2} ETag={3} to Table={4}", grainType, pk, grainReference, grainState.ETag, tableName);

            var entity = new GrainStateEntity { PartitionKey = pk, RowKey = grainType };
            ConvertToStorageFormat(grainState.State, entity);
            var record = new GrainStateRecord { Entity = entity, ETag = grainState.ETag };
            try
            {
                await tableDataManager.Write(record);
                grainState.ETag = record.ETag;
            }
            catch (Exception exc)
            {
                Log.Error((int)AzureProviderErrorCode.AzureTableProvider_WriteError, string.Format("Error Writing: GrainType={0} Grainid={1} ETag={2} to Table={3} Exception={4}",
                    grainType, grainReference, grainState.ETag, tableName, exc.Message), exc);
                throw;
            }
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
            if (tableDataManager == null) throw new ArgumentException("GrainState-Table property not initialized");

            string pk = GetKeyString(grainReference);
            if (Log.IsVerbose3) Log.Verbose3((int)AzureProviderErrorCode.AzureTableProvider_WritingData, "Clearing: GrainType={0} Pk={1} Grainid={2} ETag={3} DeleteStateOnClear={4} from Table={5}", grainType, pk, grainReference, grainState.ETag, isDeleteStateOnClear, tableName);
            var entity = new GrainStateEntity { PartitionKey = pk, RowKey = grainType };
            var record = new GrainStateRecord { Entity = entity, ETag = grainState.ETag };
            string operation = "Clearing";
            try
            {
                if (isDeleteStateOnClear)
                {
                    operation = "Deleting";
                    await tableDataManager.Delete(record);
                }
                else
                {
                    await tableDataManager.Write(record);
                }

                grainState.ETag = record.ETag; // Update in-memory data to the new ETag
            }
            catch (Exception exc)
            {
                Log.Error((int)AzureProviderErrorCode.AzureTableProvider_DeleteError, string.Format("Error {0}: GrainType={1} Grainid={2} ETag={3} from Table={4} Exception={5}",
                    operation, grainType, grainReference, grainState.ETag, tableName, exc.Message), exc);
                throw;
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
        internal void ConvertToStorageFormat(object grainState, GrainStateEntity entity)
        {
            int dataSize;

            if (useJsonFormat)
            {
                // http://james.newtonking.com/json/help/index.html?topic=html/T_Newtonsoft_Json_JsonConvert.htm
                string data = Newtonsoft.Json.JsonConvert.SerializeObject(grainState, jsonSettings);

                if (Log.IsVerbose3) Log.Verbose3("Writing JSON data size = {0} for grain id = Partition={1} / Row={2}",
                    data.Length, entity.PartitionKey, entity.RowKey);
                
                dataSize = data.Length;
                entity.StringData = data;
            }
            else
            {
                // Convert to binary format

                byte[] data = SerializationManager.SerializeToByteArray(grainState);

                if (Log.IsVerbose3) Log.Verbose3("Writing binary data size = {0} for grain id = Partition={1} / Row={2}",
                    data.Length, entity.PartitionKey, entity.RowKey);
                
                dataSize = data.Length;
                entity.Data = data;
            }
            if (dataSize > MAX_DATA_SIZE)
            {
                var msg = string.Format("Data too large to write to Azure table. Size={0} MaxSize={1}", dataSize, MAX_DATA_SIZE);
                Log.Error(0, msg);
                throw new ArgumentOutOfRangeException("GrainState.Size", msg);
            }
        }

        /// <summary>
        /// Deserialize from Azure storage format
        /// </summary>
        /// <param name="entity">The Azure table entity the stored data</param>
        internal object ConvertFromStorageFormat(GrainStateEntity entity)
        {
            object dataValue = null;
            try
            {
                if (entity.Data != null)
                {
                    // Rehydrate
                    dataValue = SerializationManager.DeserializeFromByteArray<object>(entity.Data);
                }
                else if (entity.StringData != null)
                {
                    dataValue = Newtonsoft.Json.JsonConvert.DeserializeObject<object>(entity.StringData, jsonSettings);
                } 

                // Else, no data found
            }
            catch (Exception exc)
            {
                var sb = new StringBuilder();
                if (entity.Data != null)
                {
                    sb.AppendFormat("Unable to convert from storage format GrainStateEntity.Data={0}", entity.Data);
                }
                else if (entity.StringData != null)
                {
                    sb.AppendFormat("Unable to convert from storage format GrainStateEntity.StringData={0}", entity.StringData);
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

        private string GetKeyString(GrainReference grainReference)
        {
            var key = String.Format("{0}_{1}", serviceId, grainReference.ToKeyString());
            return AzureStorageUtils.SanitizeTableProperty(key);
        }


        [Serializable]        
        internal class GrainStateEntity : TableEntity
        {
            public byte[] Data { get; set; }
            public string StringData { get; set; }
        }


        internal class GrainStateRecord
        {
            public string ETag { get; set; }
            public GrainStateEntity Entity { get; set; }
        }
        

        private class GrainStateTableDataManager
        {
            public string TableName { get; private set; }
            private readonly AzureTableDataManager<GrainStateEntity> tableManager;
            private readonly Logger logger;

            public GrainStateTableDataManager(string tableName, string storageConnectionString, Logger logger)
            {
                this.logger = logger;
                TableName = tableName;
                tableManager = new AzureTableDataManager<GrainStateEntity>(tableName, storageConnectionString);
            }

            public Task InitTableAsync()
            {
                return tableManager.InitTableAsync();
            }

            public async Task<GrainStateRecord> Read(string partitionKey, string rowKey)
            {
                if (logger.IsVerbose3) logger.Verbose3((int)AzureProviderErrorCode.AzureTableProvider_Storage_Reading, "Reading: PartitionKey={0} RowKey={1} from Table={2}", partitionKey, rowKey, TableName);
                try
                {
                    Tuple<GrainStateEntity, string> data = await tableManager.ReadSingleTableEntryAsync(partitionKey, rowKey);
                    if (data == null || data.Item1 == null)
                    {
                        if (logger.IsVerbose2) logger.Verbose2((int)AzureProviderErrorCode.AzureTableProvider_DataNotFound, "DataNotFound reading: PartitionKey={0} RowKey={1} from Table={2}", partitionKey, rowKey, TableName);
                        return null;
                    }
                    GrainStateEntity stateEntity = data.Item1;
                    var record = new GrainStateRecord { Entity = stateEntity, ETag = data.Item2 };
                    if (logger.IsVerbose3) logger.Verbose3((int)AzureProviderErrorCode.AzureTableProvider_Storage_DataRead, "Read: PartitionKey={0} RowKey={1} from Table={2} with ETag={3}", stateEntity.PartitionKey, stateEntity.RowKey, TableName, record.ETag);
                    return record;
                }
                catch (Exception exc)
                {
                    if (AzureStorageUtils.TableStorageDataNotFound(exc))
                    {
                        if (logger.IsVerbose2) logger.Verbose2((int)AzureProviderErrorCode.AzureTableProvider_DataNotFound, "DataNotFound reading (exception): PartitionKey={0} RowKey={1} from Table={2} Exception={3}", partitionKey, rowKey, TableName, TraceLogger.PrintException(exc));
                        return null;  // No data
                    }
                    throw;
                }
            }

            public async Task Write(GrainStateRecord record)
            {
                GrainStateEntity entity = record.Entity;
                if (logger.IsVerbose3) logger.Verbose3((int)AzureProviderErrorCode.AzureTableProvider_Storage_Writing, "Writing: PartitionKey={0} RowKey={1} to Table={2} with ETag={3}", entity.PartitionKey, entity.RowKey, TableName, record.ETag);
                string eTag = String.IsNullOrEmpty(record.ETag) ?
                    await tableManager.CreateTableEntryAsync(record.Entity) :
                    await tableManager.UpdateTableEntryAsync(entity, record.ETag);
                record.ETag = eTag;
            }

            public async Task Delete(GrainStateRecord record)
            {
                GrainStateEntity entity = record.Entity;
                if (logger.IsVerbose3) logger.Verbose3((int)AzureProviderErrorCode.AzureTableProvider_Storage_Writing, "Deleting: PartitionKey={0} RowKey={1} from Table={2} with ETag={3}", entity.PartitionKey, entity.RowKey, TableName, record.ETag);
                await tableManager.DeleteTableEntryAsync(entity, record.ETag);
                record.ETag = null;
            }
        }

        /// <summary> Decodes Storage exceptions.</summary>
        public bool DecodeException(Exception e, out HttpStatusCode httpStatusCode, out string restStatus, bool getRESTErrors = false)
        {
            return AzureStorageUtils.EvaluateException(e, out httpStatusCode, out restStatus, getRESTErrors);
        }
    }
}
