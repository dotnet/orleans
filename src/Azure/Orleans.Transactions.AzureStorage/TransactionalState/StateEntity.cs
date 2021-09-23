using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Azure;
using Azure.Data.Tables;
using Newtonsoft.Json;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.AzureStorage
{
    internal readonly struct StateEntity
    {
        // Each property can hold 64KB of data and each entity can take 1MB in total, so 15 full properties take
        // 15 * 64 = 960 KB leaving room for the primary key, timestamp etc
        private const int MAX_DATA_CHUNK_SIZE = 64 * 1024;
        private const int MAX_STRING_PROPERTY_LENGTH = 32 * 1024;
        private const int MAX_DATA_CHUNKS_COUNT = 15;
        private const string STRING_DATA_PROPERTY_NAME_PREFIX = nameof(StateJson);

        private static readonly string[] StringDataPropertyNames = GetPropertyNames().ToArray();

        public TableEntity Entity { get; }

        public StateEntity(TableEntity entity) => Entity = entity;

        public string PartitionKey => Entity.PartitionKey;
        public string RowKey => Entity.RowKey;
        public DateTimeOffset? Timestamp => Entity.Timestamp;
        public ETag ETag => Entity.ETag;

        public static string MakeRowKey(long sequenceId)
        {
            return $"{RK_PREFIX}{sequenceId.ToString("x16")}";
        }

        public long SequenceId => long.Parse(this.RowKey.Substring(RK_PREFIX.Length), NumberStyles.AllowHexSpecifier);

        // Row keys range from s0000000000000001 to s7fffffffffffffff
        public const string RK_PREFIX = "s_";
        public const string RK_MIN = RK_PREFIX;
        public const string RK_MAX = RK_PREFIX + "~";

        public string TransactionId
        {
            get => this.GetPropertyOrDefault(nameof(this.TransactionId)) as string;
            set => this.Entity[nameof(this.TransactionId)] = value;
        }

        public DateTime TransactionTimestamp
        {
            get => this.Entity.GetDateTimeOffset(nameof(this.TransactionTimestamp)).GetValueOrDefault().UtcDateTime;
            set => this.Entity[nameof(this.TransactionTimestamp)] = new DateTimeOffset(value.ToUniversalTime());
        }

        public string TransactionManager
        {
            get => this.GetPropertyOrDefault(nameof(this.TransactionManager)) as string;
            set => this.Entity[nameof(this.TransactionManager)] = value;
        }

        public string StateJson { get => this.GetStateInternal(); set => this.SetStateInternal(value); }

        public static StateEntity Create<T>(JsonSerializerSettings JsonSettings,
            string partitionKey, PendingTransactionState<T> pendingState)
            where T : class, new()
        {
            var entity = new TableEntity(partitionKey, MakeRowKey(pendingState.SequenceId));
            var result = new StateEntity(entity)
            {
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
                this.Entity.Remove(key);
            }

            foreach (var entry in SplitStringData(stringData))
            {
                this.Entity[entry.Key] = entry.Value;
            }

            static IEnumerable<KeyValuePair<string, object>> SplitStringData(string stringData)
            {
                if (string.IsNullOrEmpty(stringData)) yield break;

                var columnIndex = 0;
                var stringStartIndex = 0;
                while (stringStartIndex < stringData.Length)
                {
                    var chunkSize = Math.Min(MAX_STRING_PROPERTY_LENGTH, stringData.Length - stringStartIndex);

                    var key = StringDataPropertyNames[columnIndex];
                    var value = stringData.Substring(stringStartIndex, chunkSize);
                    yield return new KeyValuePair<string, object>(key, value);

                    columnIndex++;
                    stringStartIndex += chunkSize;
                }
            }
        }

        private string GetStateInternal()
        {
            return string.Concat(ReadStringDataChunks(this.Entity));

            static IEnumerable<string> ReadStringDataChunks(IDictionary<string, object> properties)
            {
                foreach (var stringDataPropertyName in StringDataPropertyNames)
                {
                    if (properties.TryGetValue(stringDataPropertyName, out var dataProperty))
                    {
                        if (dataProperty is string { Length: >0 } data)
                        {
                            yield return data;
                        }
                    }
                }
            }
        }

        private object GetPropertyOrDefault(string key)
        {
            this.Entity.TryGetValue(key, out var result);
            return result;
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
