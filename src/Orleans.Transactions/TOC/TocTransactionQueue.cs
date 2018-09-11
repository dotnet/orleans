using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.State;

namespace Orleans.Transactions.TOC
{
    internal class TocTransactionQueue<TService> : TransactionQueue<TransactionCommitter<TService>.OperationState>
                where TService : class
    {
        private TService service;

        public TocTransactionQueue(
            TService service,
            IOptions<TransactionalStateOptions> options,
            ParticipantId resource,
            Action deactivate,
            ITransactionalStateStorage<TransactionCommitter<TService>.OperationState> storage,
            JsonSerializerSettings serializerSettings,
            IClock clock,
            ILogger logger)
            : base(options, resource, deactivate, storage, serializerSettings, clock, logger)
        {
            this.service = service;
        }

        protected override void OnLocalCommit(TransactionRecord<TransactionCommitter<TService>.OperationState> entry)
        {
            CallThenLocalCommit(entry).Ignore();
        }

        private async Task CallThenLocalCommit(TransactionRecord<TransactionCommitter<TService>.OperationState> entry)
        {
            try
            {
                if (await entry.State.Operation.Commit(entry.TransactionId, this.service))
                {
                    base.OnLocalCommit(entry);
                }
                else
                {
                    base.problemFlag = TransactionalStatus.CommitFailure;
                }
            }
            catch (Exception ex)
            {
                base.logger.LogWarning(ex, $"Commit operation failed for transaction {entry.TransactionId}");
                base.problemFlag = TransactionalStatus.UnknownException;
            }
        }
    }
}
