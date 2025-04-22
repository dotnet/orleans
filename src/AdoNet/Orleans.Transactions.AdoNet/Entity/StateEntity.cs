using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.AdoNet.Entity
{
    internal class StateEntity: IEntity
    {
        public string ETag { get; set; }
        public string StateId { get; set; }
        public string RowKey { get; set; }

        public DateTimeOffset? Timestamp { get; set; }

        public string TransactionId { get; set; }

        public DateTime TransactionTimestamp { get; set; }

        public string TransactionManager{ get; set; }

        public string StateJson { get; set; }
        [NotMapped]
        public long SequenceId { get; set; }


        // Row keys range from s0000000000000001 to s7fffffffffffffff
        public const string RK_PREFIX = "s_";
        public const string RK_MIN = RK_PREFIX;
        public const string RK_MAX = RK_PREFIX + "~";

        public static string MakeRowKey(long sequenceId)
        {
            return $"{RK_PREFIX}{sequenceId.ToString("x16")}";
        }

        public static long GetSequenceId(string rowKey)
        {
            return long.Parse(rowKey[RK_PREFIX.Length..], NumberStyles.AllowHexSpecifier);
        }

        public static StateEntity Create<T>(JsonSerializerSettings JsonSettings,
           string partitionKey, PendingTransactionState<T> pendingState)
           where T : class, new()
        {
            string rowKey = MakeRowKey(pendingState.SequenceId);
            var result = new StateEntity()
            {
                StateId = partitionKey,
                SequenceId = pendingState.SequenceId,
                TransactionId = pendingState.TransactionId,
                TransactionTimestamp = pendingState.TimeStamp,
                TransactionManager = JsonConvert.SerializeObject(pendingState.TransactionManager, JsonSettings),
                StateJson = JsonConvert.SerializeObject(pendingState.State, JsonSettings),
                Timestamp = DateTime.Now,
                RowKey = rowKey,
                ETag = Guid.NewGuid().ToString(),
            };

            return result;
        }

    }
}
