
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Coordinates the prepare and commit protocol for a transaction.
    /// </summary>
    public interface ITransactionManager
    {
        /// <summary>
        /// Request sent by TA to TM. The TM responds after committing or aborting the transaction.
        /// </summary>
        /// <param name="transactionId">The identifier of the transaction to prepare.</param>
        /// <param name="accessCount">The number of reads and writes performed on this participant by the transaction.</param>
        /// <param name="timeStamp">The transaction commit timestamp.</param>
        /// <param name="writerResources">The participants which wrote during the transaction.</param>
        /// <param name="totalParticipants">The total number of participants in the transaction.</param>
        /// <returns>A task whose result is the final transaction status.</returns>
        Task<TransactionalStatus> PrepareAndCommit(Guid transactionId, AccessCounter accessCount, DateTime timeStamp,
            List<ParticipantId> writerResources, int totalParticipants);

        /// <summary>
        /// Prepares and commits a transaction.
        /// </summary>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="accessCount">The number of resource accesses performed by the transaction.</param>
        /// <param name="timeStamp">The commit timestamp.</param>
        /// <param name="writerResources">The participants which wrote during the transaction.</param>
        /// <param name="totalParticipants">The total number of participants.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The transaction status.</returns>
        Task<TransactionalStatus> PrepareAndCommit(
            Guid transactionId,
            AccessCounter accessCount,
            DateTime timeStamp,
            List<ParticipantId> writerResources,
            int totalParticipants,
            CancellationToken cancellationToken)
            => PrepareAndCommit(transactionId, accessCount, timeStamp, writerResources, totalParticipants);

        /// <summary>
        /// One-way message sent by a participant to the TM after it (successfully or unsuccessfully) prepares.
        /// </summary>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="timeStamp">The transaction commit timestamp.</param>
        /// <param name="resource">The participant reporting its prepare result.</param>
        /// <param name="status">The participant's prepare result.</param>
        /// <returns>A task which represents the operation.</returns>
        Task Prepared(Guid transactionId, DateTime timeStamp, ParticipantId resource, TransactionalStatus status);

        /// <summary>
        /// Reports that a participant has prepared.
        /// </summary>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="timeStamp">The commit timestamp.</param>
        /// <param name="resource">The reporting participant.</param>
        /// <param name="status">The prepare status.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task Prepared(Guid transactionId, DateTime timeStamp, ParticipantId resource, TransactionalStatus status, CancellationToken cancellationToken)
            => Prepared(transactionId, timeStamp, resource, status);

        /// <summary>
        /// One-way message sent by participants to TM, to let TM know they are still waiting to hear about
        /// the fate of a transaction.
        /// </summary>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="timeStamp">The transaction commit timestamp.</param>
        /// <param name="resource">The participant awaiting the transaction outcome.</param>
        /// <returns>A task which represents the operation.</returns>
        Task Ping(Guid transactionId, DateTime timeStamp, ParticipantId resource);

        /// <summary>
        /// Reports that a participant is awaiting the outcome of a transaction.
        /// </summary>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="timeStamp">The commit timestamp.</param>
        /// <param name="resource">The reporting participant.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task Ping(Guid transactionId, DateTime timeStamp, ParticipantId resource, CancellationToken cancellationToken)
            => Ping(transactionId, timeStamp, resource);
    }

    /// <summary>
    /// Counts read and write accesses on a transaction participant.
    /// </summary>
    [GenerateSerializer]
    [Serializable]
    public struct AccessCounter
    {
        /// <summary>
        /// The number of read accesses.
        /// </summary>
        [Id(0)]
        public int Reads;

        /// <summary>
        /// The number of write accesses.
        /// </summary>
        [Id(1)]
        public int Writes;

        /// <summary>
        /// Adds the read and write counts from two values.
        /// </summary>
        /// <param name="c1">The first access count.</param>
        /// <param name="c2">The second access count.</param>
        /// <returns>The combined access count.</returns>
        public static AccessCounter operator +(AccessCounter c1, AccessCounter c2)
        {
            return new AccessCounter { Reads = c1.Reads + c2.Reads, Writes = c1.Writes + c2.Writes };
        }
    }
}



