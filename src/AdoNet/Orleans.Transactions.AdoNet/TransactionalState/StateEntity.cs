using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.AdoNet.TransactionalState
{
    public class StateEntity
    {
        public string StateId { get; set; }
        public long SequenceId { get; set; } //=> long.Parse(this.RowKey[RK_PREFIX.Length..], NumberStyles.AllowHexSpecifier);
        public string TransactionId { get; set; }

        public DateTime TransactionTimestamp { get; set; }

        public string TransactionManager{ get; set; }

        public string StateJson { get; set; }//{ get => this.GetStateInternal(); set => this.SetStateInternal(value); }

        public string RowKey { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public string ETag { get; set; }



        // Each property can hold 64KB of data and each entity can take 1MB in total, so 15 full properties take
        // 15 * 64 = 960 KB leaving room for the primary key, timestamp etc
        private const int MAX_DATA_CHUNK_SIZE = 64 * 1024;
        private const int MAX_STRING_PROPERTY_LENGTH = 32 * 1024;
        private const int MAX_DATA_CHUNKS_COUNT = 15;
        private const string STRING_DATA_PROPERTY_NAME_PREFIX = nameof(StateJson);

        private static readonly string[] StringDataPropertyNames = GetPropertyNames().ToArray();

        // Row keys range from s0000000000000001 to s7fffffffffffffff
        public const string RK_PREFIX = "s_";
        public const string RK_MIN = RK_PREFIX;
        public const string RK_MAX = RK_PREFIX + "~";

        public static string MakeRowKey(long sequenceId)
        {
            return $"{RK_PREFIX}{sequenceId.ToString("x16")}";
        }

        public static StateEntity Create<T>(JsonSerializerSettings JsonSettings,
           string partitionKey, PendingTransactionState<T> pendingState)
           where T : class, new()
        {
            var tbEntity = new TableEntity(partitionKey, MakeRowKey(pendingState.SequenceId));
            var result = new StateEntity()
            {
                StateId = partitionKey,
                SequenceId = long.Parse(tbEntity.RowKey[RK_PREFIX.Length..], NumberStyles.AllowHexSpecifier),
                TransactionId = pendingState.TransactionId,
                TransactionTimestamp = pendingState.TimeStamp,
                TransactionManager = JsonConvert.SerializeObject(pendingState.TransactionManager, JsonSettings),
                StateJson = JsonConvert.SerializeObject(pendingState.State, JsonSettings),
            };

            tbEntity = result.SetState(tbEntity, pendingState.State, JsonSettings);
            result.Timestamp = DateTime.Now;//tbEntity.Timestamp;
            result.ETag = tbEntity.ETag;
            result.RowKey = tbEntity.RowKey;
            return result;
        }


        public T GetState<T>(TableEntity tbEntity,JsonSerializerSettings jsonSettings)
        {
            return JsonConvert.DeserializeObject<T>(this.GetStateInternal(tbEntity), jsonSettings);
        }

        public TableEntity SetState<T>(TableEntity tbEntity, T state, JsonSerializerSettings jsonSettings)
        {
           return this.SetStateInternal(tbEntity,JsonConvert.SerializeObject(state, jsonSettings));
        }

        private TableEntity SetStateInternal(TableEntity tbEntity,string stringData)
        {
            CheckMaxDataSize((stringData ?? string.Empty).Length * 2, MAX_DATA_CHUNK_SIZE * MAX_DATA_CHUNKS_COUNT);


            foreach (var key in StringDataPropertyNames)
            {
                tbEntity.Remove(key);
            }

            foreach (var entry in SplitStringData(stringData))
            {
                tbEntity[entry.Key] = entry.Value;
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

            return tbEntity;
        }

        private string GetStateInternal(TableEntity tbEntity)
        {
            return string.Concat(ReadStringDataChunks(tbEntity));

            static IEnumerable<string> ReadStringDataChunks(IDictionary<string, object> properties)
            {
                foreach (var stringDataPropertyName in StringDataPropertyNames)
                {
                    if (properties.TryGetValue(stringDataPropertyName, out var dataProperty))
                    {
                        if (dataProperty is string { Length: > 0 } data)
                        {
                            yield return data;
                        }
                    }
                }
            }
        }

        private static object GetPropertyOrDefault(TableEntity tbEntity, string key)
        {
            tbEntity.TryGetValue(key, out var result);
            return result;
        }

        private static void CheckMaxDataSize(int dataSize, int maxDataSize)
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
