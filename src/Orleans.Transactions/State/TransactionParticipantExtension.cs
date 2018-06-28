
using Orleans.Transactions.Abstractions;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Orleans.Transactions
{
    public class TransactionParticipantExtension : ITransactionParticipantExtension
    {
        private readonly Dictionary<string, ITransactionParticipant> localParticipants = new Dictionary<string, ITransactionParticipant>();

        public void Register(string resourceId, ITransactionParticipant localTransactionParticipant)
        {
            this.localParticipants.Add(resourceId, localTransactionParticipant);
        }

        #region request forwarding

        public Task Abort(string resourceId, Guid transactionId)
        {
            return localParticipants[resourceId].Abort(transactionId);
        }

        public Task Cancel(string resourceId, Guid transactionId, DateTime timeStamp, TransactionalStatus status)
        {
            return localParticipants[resourceId].Cancel(transactionId, timeStamp, status);
        }

        public Task<TransactionalStatus> CommitReadOnly(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp)
        {
            return localParticipants[resourceId].CommitReadOnly(transactionId, accessCount, timeStamp);
        }

        public Task Confirm(string resourceId, Guid transactionId, DateTime timeStamp)
        {
            return localParticipants[resourceId].Confirm(transactionId, timeStamp);
        }

        public Task Ping(string resourceId, Guid transactionId, DateTime timeStamp, ITransactionParticipant participant)
        {
            return localParticipants[resourceId].Ping(transactionId, timeStamp, participant);
        }

        public Task Prepare(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ITransactionParticipant transactionManager)
        {
            return localParticipants[resourceId].Prepare(transactionId, accessCount, timeStamp, transactionManager);
        }

        public Task<TransactionalStatus> PrepareAndCommit(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ITransactionParticipant> writeParticipants, int totalParticipants)
        {
            return localParticipants[resourceId].PrepareAndCommit(transactionId, accessCount, timeStamp, writeParticipants, totalParticipants);
        }

        public Task Prepared(string resourceId, Guid transactionId, DateTime timestamp, ITransactionParticipant participant, TransactionalStatus status)
        {
            return localParticipants[resourceId].Prepared(transactionId, timestamp, participant, status);
        }

        #endregion
    }
}
