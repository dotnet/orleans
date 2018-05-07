using Microsoft.WindowsAzure.Storage.Table;
using Newtonsoft.Json;
using Orleans.Transactions.Abstractions;
using System;

namespace Orleans.Transactions.DistributedTM.AzureStorage
{
    internal class StateEntity : TableEntity
    {
        public static string MakeRowKey(long sequenceId)
        {
            return sequenceId.ToString("x16");
        }

        public long SequenceId => long.Parse(RowKey);

        // row keys range from 0000000000000001 to 7fffffffffffffff
        public const string RK_MIN = "0";
        public const string RK_MAX = "8";

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
