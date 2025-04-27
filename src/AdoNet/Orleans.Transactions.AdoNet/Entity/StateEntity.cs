using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.AdoNet.Utils;

namespace Orleans.Transactions.AdoNet.Entity
{
    internal class StateEntity : IEntity
    {
        public string StateId { get; set; }
        public long SequenceId { get; set; }

        public DateTimeOffset? Timestamp { get; set; }

        public string TransactionId { get; set; }

        public DateTimeOffset? TransactionTimestamp { get; set; }

        public byte[] TransactionManager { get; set; }

        public byte[] SateData { get; set; }

        public string ETag { get; set; }

        public static StateEntity Create<T>(JsonSerializerSettings jsonSettings,
           string partitionKey, PendingTransactionState<T> pendingState)
           where T : class, new()
        {
            var result = new StateEntity()
            {
                StateId = partitionKey,
                SequenceId = pendingState.SequenceId,
                TransactionId = pendingState.TransactionId,
                TransactionTimestamp = new DateTimeOffset(pendingState.TimeStamp).ToUniversalTime(),
                TransactionManager = JsonUtils.SerializeWithNewtonsoftJson(pendingState.TransactionManager,jsonSettings),
                SateData = JsonUtils.SerializeWithNewtonsoftJson(pendingState.State, jsonSettings),
            };

            return result;
        }
    }
}
