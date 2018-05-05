using Microsoft.WindowsAzure.Storage.Table;
using Newtonsoft.Json;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.AzureStorage
{
    internal class StateEntity : TableEntity
    {
        public const string RKMin = "ts_";
        public const string RKMax = "ts_~";

        public string TransactionId { get; set; }
        public long SequenceId { get; set; }
        public string StateJson { get; set; }

        public static StateEntity Create<T>(JsonSerializerSettings JsonSettings,
            string partitionKey, PendingTransactionState<T> pendingState)
            where T : class, new()
        {
            return new StateEntity
            {
                PartitionKey = partitionKey,
                RowKey = MakeRowKey(pendingState.TransactionId, pendingState.SequenceId),
                TransactionId = pendingState.TransactionId,
                SequenceId = pendingState.SequenceId,
                StateJson = JsonConvert.SerializeObject(pendingState.State, JsonSettings)
            };
        }

        public static string MakeRowKey(string TransactionId, long sequenceId)
        {
            return AzureStorageUtils.SanitizeTableProperty($"{RKMin}{TransactionId}_{sequenceId.ToString("x16")}");
        }

        public T GetState<T>(JsonSerializerSettings JsonSettings)
        {
            return JsonConvert.DeserializeObject<T>(this.StateJson, JsonSettings);
        }
    }
}
