using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Azure.Cosmos.Table;
using Newtonsoft.Json;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.AzureStorage
{
    internal class StateEntity : ITableEntity
    {
        // Each property can hold 64KB of data and each entity can take 1MB in total, so 15 full properties take
        // 15 * 64 = 960 KB leaving room for the primary key, timestamp etc
        private const int MAX_DATA_CHUNK_SIZE = 64 * 1024;
        private const int MAX_STRING_PROPERTY_LENGTH = 32 * 1024;
        private const int MAX_DATA_CHUNKS_COUNT = 15;
        private const string STRING_DATA_PROPERTY_NAME_PREFIX = nameof(StateJson);

        private static readonly string[] StringDataPropertyNames = GetPropertyNames().ToArray();

        private IDictionary<string, EntityProperty> properties = new Dictionary<string, EntityProperty>();

        public static string MakeRowKey(long sequenceId)
        {
            return $"{RK_PREFIX}{sequenceId.ToString("x16")}";
        }

        public string RowKey { get; set; }

        public string PartitionKey { get; set; }

        public long SequenceId => long.Parse(this.RowKey.Substring(RK_PREFIX.Length), NumberStyles.AllowHexSpecifier);

        // Row keys range from s0000000000000001 to s7fffffffffffffff
        public const string RK_PREFIX = "s_";
        public const string RK_MIN = RK_PREFIX;
        public const string RK_MAX = RK_PREFIX + "~";

        public string TransactionId
        {
            get => this.GetPropertyOrDefault(nameof(this.TransactionId))?.StringValue;
            set => this.properties[nameof(this.TransactionId)] = new EntityProperty(value);
        }

        public DateTime TransactionTimestamp
        {
            get => this.GetPropertyOrDefault(nameof(this.TransactionTimestamp))?.DateTime ?? default;
            set => this.properties[nameof(this.TransactionTimestamp)] = new EntityProperty(value);
        }

        public string TransactionManager
        {
            get => this.GetPropertyOrDefault(nameof(this.TransactionManager))?.StringValue;
            set => this.properties[nameof(this.TransactionManager)] = new EntityProperty(value);
        }

        public string StateJson { get => this.GetStateInternal(); set => this.SetStateInternal(value); }

        public DateTimeOffset Timestamp { get; set; }

        public string ETag { get; set; }

        public static StateEntity Create<T>(JsonSerializerSettings JsonSettings,
            string partitionKey, PendingTransactionState<T> pendingState)
            where T : class, new()
        {
            var result = new StateEntity
            {
                PartitionKey = partitionKey,
                RowKey = MakeRowKey(pendingState.SequenceId),
                TransactionId = pendingState.TransactionId,
                TransactionTimestamp = pendingState.TimeStamp,
                TransactionManager = JsonConvert.SerializeObject(pendingState.TransactionManager, JsonSettings),
            };

            result.SetState(pendingState.State, JsonSettings);
            return result;
        }

        public T GetState<T>(JsonSerializerSettings jsonSettings)
        {
            return JsonConvert.DeserializeObject<T>(this.GetStateInternal(), jsonSettings);
        }

        public void SetState<T>(T state, JsonSerializerSettings jsonSettings)
        {
            this.SetStateInternal(JsonConvert.SerializeObject(state, jsonSettings));
        }

        private void SetStateInternal(string stringData)
        {
            this.CheckMaxDataSize((stringData ?? string.Empty).Length * 2, MAX_DATA_CHUNK_SIZE * MAX_DATA_CHUNKS_COUNT);

            foreach (var key in StringDataPropertyNames)
            {
                this.properties.Remove(key);
            }

            foreach (var entry in SplitStringData(stringData))
            {
                this.properties.Add(entry);
            }

            static IEnumerable<KeyValuePair<string, EntityProperty>> SplitStringData(string stringData)
            {
                if (string.IsNullOrEmpty(stringData)) yield break;

                var columnIndex = 0;
                var stringStartIndex = 0;
                while (stringStartIndex < stringData.Length)
                {
                    var chunkSize = Math.Min(MAX_STRING_PROPERTY_LENGTH, stringData.Length - stringStartIndex);

                    var key = StringDataPropertyNames[columnIndex];
                    var value = new EntityProperty(stringData.Substring(stringStartIndex, chunkSize));
                    yield return new KeyValuePair<string, EntityProperty>(key, value);

                    columnIndex++;
                    stringStartIndex += chunkSize;
                }
            }
        }

        private string GetStateInternal()
        {
            return string.Concat(ReadStringDataChunks(this.properties));

            static IEnumerable<string> ReadStringDataChunks(IDictionary<string, EntityProperty> properties)
            {
                foreach (var stringDataPropertyName in StringDataPropertyNames)
                {
                    EntityProperty dataProperty;
                    if (properties.TryGetValue(stringDataPropertyName, out dataProperty))
                    {
                        var data = dataProperty.StringValue;
                        if (!string.IsNullOrEmpty(data))
                        {
                            yield return data;
                        }
                    }
                }
            }
        }

        private EntityProperty GetPropertyOrDefault(string key)
        {
            this.properties.TryGetValue(key, out var result);
            return result;
        }

        public void ReadEntity(IDictionary<string, EntityProperty> properties, OperationContext operationContext)
        {
            this.properties = properties;
        }

        public IDictionary<string, EntityProperty> WriteEntity(OperationContext operationContext)
        {
            return this.properties;
        }

        private void CheckMaxDataSize(int dataSize, int maxDataSize)
        {
            if (dataSize > maxDataSize)
            {
                var msg = string.Format("Data too large to write to table. Size={0} MaxSize={1}", dataSize, maxDataSize);
                throw new ArgumentOutOfRangeException("state", msg);
            }
        }

        private static IEnumerable<string> GetPropertyNames()
        {
            yield return STRING_DATA_PROPERTY_NAME_PREFIX;
            for (var i = 1; i < MAX_DATA_CHUNKS_COUNT; ++i)
            {
                yield return STRING_DATA_PROPERTY_NAME_PREFIX + i.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
