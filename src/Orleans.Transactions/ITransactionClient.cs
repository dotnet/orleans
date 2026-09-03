using System;
using System.Threading.Tasks;

namespace Orleans;

/// <summary>
/// Executes delegates using Orleans transaction semantics.
/// </summary>
public interface ITransactionClient
{
    /// <summary>
    /// Executes a delegate using the specified transaction option. A newly created transaction commits when the delegate completes successfully; a joined transaction records this participant's commit vote.
    /// </summary>
    /// <param name="transactionOption">The transaction creation, joining, or suppression behavior.</param>
    /// <param name="transactionDelegate">The delegate to execute.</param>
    /// <returns>A <see cref="Task"/> representing the transaction operation.</returns>
    Task RunTransaction(TransactionOption transactionOption, Func<Task> transactionDelegate);

    /// <summary>
    /// Executes a delegate using the specified transaction option.
    /// </summary>
    /// <param name="transactionOption">The transaction creation, joining, or suppression behavior.</param>
    /// <param name="transactionDelegate">
    /// The delegate to execute. Its result indicates whether the transaction should commit.
    /// </param>
    /// <returns>A <see cref="Task"/> representing the transaction operation.</returns>
    Task RunTransaction(TransactionOption transactionOption, Func<Task<bool>> transactionDelegate);

    /// <summary>
    /// Executes a delegate using the specified transaction option. A newly created transaction commits when the delegate completes successfully; a joined transaction records this participant's commit vote.
    /// </summary>
    /// <param name="transactionOption">The transaction creation, joining, or suppression behavior.</param>
    /// <param name="transactionDelegate">The delegate to execute.</param>
    /// <param name="useExclusiveLock">When <see langword="true"/>, all transactional states accessed during this transaction
    /// will acquire exclusive locks even for read operations, preventing lock upgrade conflicts under high contention.</param>
    /// <returns>A <see cref="Task"/> representing the transaction operation.</returns>
    Task RunTransaction(TransactionOption transactionOption, Func<Task> transactionDelegate, bool useExclusiveLock);

    /// <summary>
    /// Executes a delegate using the specified transaction option.
    /// </summary>
    /// <param name="transactionOption">The transaction creation, joining, or suppression behavior.</param>
    /// <param name="transactionDelegate">
    /// The delegate to execute. Its result indicates whether the transaction should commit.
    /// </param>
    /// <param name="useExclusiveLock">When <see langword="true"/>, all transactional states accessed during this transaction
    /// will acquire exclusive locks even for read operations, preventing lock upgrade conflicts under high contention.</param>
    /// <returns>A <see cref="Task"/> representing the transaction operation.</returns>
    Task RunTransaction(TransactionOption transactionOption, Func<Task<bool>> transactionDelegate, bool useExclusiveLock);
}
