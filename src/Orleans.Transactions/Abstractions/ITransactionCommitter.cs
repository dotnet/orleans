
using System;
using System.Threading.Tasks;

namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Defines an operation which is applied to a service when a transaction commits.
    /// </summary>
    /// <typeparam name="TService">The service type.</typeparam>
    public interface ITransactionCommitOperation<TService>
        where TService : class
    {
        /// <summary>
        /// Applies the operation to the service as part of committing the transaction.
        /// </summary>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="service">The service to which the operation is applied.</param>
        /// <returns>
        /// A task whose result is <see langword="true"/> when the operation completed and the transaction can be committed;
        /// otherwise, <see langword="false"/>.
        /// </returns>
        Task<bool> Commit(Guid transactionId, TService service);
    }

    /// <summary>
    /// Enlists service operations which are applied when their transactions commit.
    /// </summary>
    /// <typeparam name="TService">The service type.</typeparam>
    public interface ITransactionCommitter<TService>
        where TService : class
    {
        /// <summary>
        /// Enlists an operation to be applied when the current transaction commits.
        /// </summary>
        /// <param name="operation">The operation to enlist.</param>
        /// <returns>A task which represents the enlistment operation.</returns>
        Task OnCommit(ITransactionCommitOperation<TService> operation);
    }
}
