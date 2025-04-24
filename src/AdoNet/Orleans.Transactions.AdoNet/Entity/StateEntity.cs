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
        public string StateId { get; set; }
        public long SequenceId { get; set; }

        public DateTimeOffset? Timestamp { get; set; }

        public string TransactionId { get; set; }

        public DateTime TransactionTimestamp { get; set; }

        public string TransactionManager{ get; set; }

        public string StateJson { get; set; }

        public string ETag { get; set; }

        public static StateEntity Create<T>(JsonSerializerSettings JsonSettings,
           string partitionKey, PendingTransactionState<T> pendingState)
           where T : class, new()
        {
            var result = new StateEntity()
            {
                StateId = partitionKey,
                SequenceId = pendingState.SequenceId,
                TransactionId = pendingState.TransactionId,
                TransactionTimestamp = pendingState.TimeStamp,
                TransactionManager = JsonConvert.SerializeObject(pendingState.TransactionManager, JsonSettings),
                StateJson = JsonConvert.SerializeObject(pendingState.State, JsonSettings),
                Timestamp = DateTime.Now,
            };

            return result;
        }

    }
}
