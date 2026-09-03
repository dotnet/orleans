using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Defines the grain extension used to dispatch transaction protocol messages to named transaction managers.
    /// </summary>
    public interface ITransactionManagerExtension : IGrainExtension
    {
        /// <summary>
        /// Requests that the specified transaction manager prepare and commit a transaction.
        /// </summary>
        /// <param name="resourceId">The identifier of the transaction manager resource.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="accessCount">The number of reads and writes performed on the transaction manager by the transaction.</param>
        /// <param name="timeStamp">The transaction commit timestamp.</param>
        /// <param name="writeResources">The participants which wrote during the transaction.</param>
        /// <param name="totalParticipants">The total number of participants in the transaction.</param>
        /// <returns>A task whose result is the final transaction status.</returns>
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [Alias("PrepareAndCommit")]
        Task<TransactionalStatus> PrepareAndCommit(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ParticipantId> writeResources, int totalParticipants);

        /// <summary>
        /// Requests that the specified transaction manager prepare and commit a transaction.
        /// </summary>
        /// <param name="resourceId">The identifier of the transaction manager resource.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="accessCount">The number of reads and writes performed on the transaction manager by the transaction.</param>
        /// <param name="timeStamp">The transaction commit timestamp.</param>
        /// <param name="writeResources">The participants which wrote during the transaction.</param>
        /// <param name="totalParticipants">The total number of participants in the transaction.</param>
        /// <param name="cancellationToken">The token used to cancel the operation.</param>
        /// <returns>A task whose result is the final transaction status.</returns>
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [Alias("B024EFA6")]
        Task<TransactionalStatus> PrepareAndCommit(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ParticipantId> writeResources, int totalParticipants, CancellationToken cancellationToken)
            => PrepareAndCommit(resourceId, transactionId, accessCount, timeStamp, writeResources, totalParticipants);

        /// <summary>
        /// Reports the result of preparing a participant to the specified transaction manager.
        /// </summary>
        /// <param name="resourceId">The identifier of the transaction manager resource.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="timestamp">The transaction commit timestamp.</param>
        /// <param name="resource">The participant reporting its prepare result.</param>
        /// <param name="status">The participant's prepare result.</param>
        /// <returns>A task which represents the one-way operation.</returns>
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [OneWay]
        [Alias("Prepared")]
        Task Prepared(string resourceId, Guid transactionId, DateTime timestamp, ParticipantId resource, TransactionalStatus status);

        /// <summary>
        /// Reports the result of preparing a participant to the specified transaction manager.
        /// </summary>
        /// <param name="resourceId">The identifier of the transaction manager resource.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="timestamp">The transaction commit timestamp.</param>
        /// <param name="resource">The participant reporting its prepare result.</param>
        /// <param name="status">The participant's prepare result.</param>
        /// <param name="cancellationToken">The token used to cancel the operation.</param>
        /// <returns>A task which represents the one-way operation.</returns>
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
