
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Interface that allows a component to be a transaction participant.
    /// </summary>
    public interface ITransactionParticipant : IEquatable<ITransactionParticipant>
    {
        /// <summary>
        /// Request sent by TA to all participants of a read-only transaction (one-phase commit). 
        /// Participants respond after committing or aborting the read.
        /// </summary>
        /// <param name="transactionId">the id of the transaction to prepare</param>
        /// <param name="accessCount">number of reads/writes performed on this participant by this transaction</param>
        /// <param name="timeStamp">the commit timestamp for this transaction</param>
        /// <returns></returns>
        Task<TransactionalStatus> CommitReadOnly(Guid transactionId, AccessCounter accessCount, DateTime timeStamp);

        /// <summary>
        /// One-way message sent by TA to all participants except TM.  
        /// </summary>
        /// <param name="transactionId">the id of the transaction to prepare</param>
        /// <param name="accessCount">number of reads/writes performed on this participant by this transaction</param>
        /// <param name="timeStamp">the commit timestamp for this transaction</param>
        /// <param name="transactionManager">the transaction manager for this transaction</param>
        /// <returns></returns>
        Task Prepare(Guid transactionId, AccessCounter accessCount,
            DateTime timeStamp, ITransactionParticipant transactionManager);

        /// <summary>
        /// Request sent by TA to TM. The TM responds after committing or aborting the transaction.
        /// </summary>
        /// <param name="transactionId">the id of the transaction to prepare</param>
        /// <param name="accessCount">number of reads/writes performed on this participant by this transaction</param>
        /// <param name="timeStamp">the commit timestamp for this transaction</param>
        /// <param name="writeParticipants">the participants who wrote during the transaction</param>
        /// <param name="totalParticipants">the total number of participants in the transaction</param>
        /// <returns>the status of the transaction</returns>
        Task<TransactionalStatus> PrepareAndCommit(Guid transactionId, AccessCounter accessCount, DateTime timeStamp,
            List<ITransactionParticipant> writeParticipants, int totalParticipants);

        /// <summary>
        /// One-way message sent by TA to participants to let them know a transaction has aborted.
        /// </summary>
        /// <param name="transactionId">The id of the aborted transaction</param>
        Task Abort(Guid transactionId);

        /// <summary>
        /// One-way message sent by a participant to the TM after it (successfully or unsuccessfully) prepares.
        /// </summary>
        /// <param name="transactionId">The id of the transaction</param>
        /// <param name="timeStamp">The commit timestamp of the transaction</param>
        /// <param name="participant">The participant sending the message</param>
        /// <param name="status">The outcome of the prepare</param>
        /// <returns></returns>
        Task Prepared(Guid transactionId, DateTime timeStamp, ITransactionParticipant participant, TransactionalStatus status);

        /// <summary>
        /// One-way message sent by TM to participants to let them know a transaction has aborted.
        /// </summary>
        /// <param name="transactionId">The id of the aborted transaction</param>
        /// <param name="timeStamp">The commit timestamp of the aborted transaction</param>
        /// <param name="status">Reason for abort</param>
        Task Cancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status);

        /// <summary>
        /// Request sent by TM to participants to let them know a transaction has committed.
        /// Participants respond after cleaning up all prepare records.
        /// </summary>
        /// <param name="transactionId">The id of the aborted transaction</param>
        /// <param name="timeStamp">The commit timestamp of the aborted transaction</param>
        Task Confirm(Guid transactionId, DateTime timeStamp);

        /// <summary>
        /// One-way message sent by participants to TM, to let TM know they are still waiting to hear about
        /// the fate of a transaction.
        /// </summary>
        /// <param name="transactionId">The id of the transaction</param>
        /// <param name="timeStamp">The commit timestamp of the transaction</param>
        /// <param name="participant">The participant sending the message</param>
        Task Ping(Guid transactionId, DateTime timeStamp, ITransactionParticipant participant);
    }


 
    /// <summary>
    /// Counts read and write accesses on a transaction participant.
    /// </summary>
    [Serializable]
    public struct AccessCounter
    {
        public int Reads;
        public int Writes;

        public static AccessCounter operator +(AccessCounter c1, AccessCounter c2)
        {
            return new AccessCounter { Reads = c1.Reads + c2.Reads, Writes = c1.Writes + c2.Writes };
        }
    }


}

  
   

