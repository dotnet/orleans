
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Transactions.Abstractions
{
    public interface ITransactionManager
    {
        /// <summary>
        /// Request sent by TA to TM. The TM responds after committing or aborting the transaction.
        /// </summary>
        /// <param name="transactionId">the id of the transaction to prepare</param>
        /// <param name="accessCount">number of reads/writes performed on this participant by this transaction</param>
        /// <param name="timeStamp">the commit timestamp for this transaction</param>
        /// <param name="writerResources">the participants who wrote during the transaction</param>
        /// <param name="totalParticipants">the total number of participants in the transaction</param>
        /// <returns>the status of the transaction</returns>
        Task<TransactionalStatus> PrepareAndCommit(Guid transactionId, AccessCounter accessCount, DateTime timeStamp,
            List<ParticipantId> writerResources, int totalParticipants);

        /// <summary>
        /// One-way message sent by a participant to the TM after it (successfully or unsuccessfully) prepares.
        /// </summary>
        /// <param name="transactionId">The id of the transaction</param>
        /// <param name="timeStamp">The commit timestamp of the transaction</param>
        /// <param name="resource">The participant sending the message</param>
        /// <param name="status">The outcome of the prepare</param>
        /// <returns></returns>
        Task Prepared(Guid transactionId, DateTime timeStamp, ParticipantId resource, TransactionalStatus status);

        /// <summary>
        /// One-way message sent by participants to TM, to let TM know they are still waiting to hear about
        /// the fate of a transaction.
        /// </summary>
        /// <param name="transactionId">The id of the transaction</param>
        /// <param name="timeStamp">The commit timestamp of the transaction</param>
        /// <param name="resource">The participant sending the message</param>
        Task Ping(Guid transactionId, DateTime timeStamp, ParticipantId resource);
    }

    /// <summary>
    /// Counts read and write accesses on a transaction participant.
    /// </summary>
    [GenerateSerializer]
    [Serializable]
    public struct AccessCounter
    {
        [Id(0)]
        public int Reads;
        [Id(1)]
        public int Writes;

        public static AccessCounter operator +(AccessCounter c1, AccessCounter c2)
        {
            return new AccessCounter { Reads = c1.Reads + c2.Reads, Writes = c1.Writes + c2.Writes };
        }
    }
}

  
   

