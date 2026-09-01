using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Transactions.Abstractions
{
    public interface ITransactionManagerExtension : IGrainExtension
    {
        /// <summary>Prepares and commits a transaction.</summary>
        /// <param name="resourceId">The transaction manager resource identifier.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="accessCount">The number of resource accesses.</param>
        /// <param name="timeStamp">The commit timestamp.</param>
        /// <param name="writeResources">The participants which wrote during the transaction.</param>
        /// <param name="totalParticipants">The total number of participants.</param>
        /// <returns>The transaction status.</returns>
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [Alias("PrepareAndCommit")]
        Task<TransactionalStatus> PrepareAndCommit(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ParticipantId> writeResources, int totalParticipants);

        /// <summary>Prepares and commits a transaction.</summary>
        /// <param name="resourceId">The transaction manager resource identifier.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="accessCount">The number of resource accesses.</param>
        /// <param name="timeStamp">The commit timestamp.</param>
        /// <param name="writeResources">The participants which wrote during the transaction.</param>
        /// <param name="totalParticipants">The total number of participants.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The transaction status.</returns>
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [Alias("B024EFA6")]
        Task<TransactionalStatus> PrepareAndCommit(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ParticipantId> writeResources, int totalParticipants, CancellationToken cancellationToken)
            => PrepareAndCommit(resourceId, transactionId, accessCount, timeStamp, writeResources, totalParticipants);

        /// <summary>Reports that a participant has prepared.</summary>
        /// <param name="resourceId">The transaction manager resource identifier.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="timestamp">The commit timestamp.</param>
        /// <param name="resource">The reporting participant.</param>
        /// <param name="status">The prepare status.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [OneWay]
        [Alias("Prepared")]
        Task Prepared(string resourceId, Guid transactionId, DateTime timestamp, ParticipantId resource, TransactionalStatus status);

        /// <summary>Reports that a participant has prepared.</summary>
        /// <param name="resourceId">The transaction manager resource identifier.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="timestamp">The commit timestamp.</param>
        /// <param name="resource">The reporting participant.</param>
        /// <param name="status">The prepare status.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [OneWay]
        [Alias("12BEFA17")]
        Task Prepared(string resourceId, Guid transactionId, DateTime timestamp, ParticipantId resource, TransactionalStatus status, CancellationToken cancellationToken)
            => Prepared(resourceId, transactionId, timestamp, resource, status);

        /// <summary>Reports that a participant is awaiting a transaction outcome.</summary>
        /// <param name="resourceId">The transaction manager resource identifier.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="timeStamp">The commit timestamp.</param>
        /// <param name="resource">The reporting participant.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [OneWay]
        [Alias("Ping")]
        Task Ping(string resourceId, Guid transactionId, DateTime timeStamp, ParticipantId resource);

        /// <summary>Reports that a participant is awaiting a transaction outcome.</summary>
        /// <param name="resourceId">The transaction manager resource identifier.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="timeStamp">The commit timestamp.</param>
        /// <param name="resource">The reporting participant.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [OneWay]
        [Alias("AC4A9AEB")]
        Task Ping(string resourceId, Guid transactionId, DateTime timeStamp, ParticipantId resource, CancellationToken cancellationToken)
            => Ping(resourceId, transactionId, timeStamp, resource);
    }
}
