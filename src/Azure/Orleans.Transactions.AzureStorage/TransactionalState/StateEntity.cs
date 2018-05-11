using System;
using Microsoft.WindowsAzure.Storage.Table;
using Newtonsoft.Json;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.AzureStorage
{
    internal class StateEntity : TableEntity
    {
        public static string MakeRowKey(long sequenceId)
        {
            return $"{RK_PREFIX}{sequenceId.ToString("x16")}";
        }

        public long SequenceId => long.Parse(RowKey.Substring(RK_PREFIX.Length));

        // row keys range from s0000000000000001 to s7fffffffffffffff
        public const string RK_PREFIX = "s_";
        public const string RK_MIN = RK_PREFIX;
        public const string RK_MAX = RK_PREFIX + "~";

        public string TransactionId { get; set; }

        public DateTime TransactionTimestamp { get; set; }

        public string TransactionManager { get; set; }

        public string StateJson { get; set; }

        public static StateEntity Create<T>(JsonSerializerSettings JsonSettings,
            string partitionKey, PendingTransactionState<T> pendingState)
            where T : class, new()
        {
            return new StateEntity
            {
                PartitionKey = partitionKey,
                RowKey = MakeRowKey(pendingState.SequenceId),
                TransactionId = pendingState.TransactionId,
                TransactionTimestamp = pendingState.TimeStamp,
                TransactionManager = pendingState.TransactionManager,
                StateJson = JsonConvert.SerializeObject(pendingState.State, JsonSettings)
            };
        }

        public T GetState<T>(JsonSerializerSettings JsonSettings)
        {
            return JsonConvert.DeserializeObject<T>(this.StateJson, JsonSettings);
        }
        public void SetState<T>(T state, JsonSerializerSettings JsonSettings)
        {
            StateJson = JsonConvert.SerializeObject(state, JsonSettings);
        }
    }
}
