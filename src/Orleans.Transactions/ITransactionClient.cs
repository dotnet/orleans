using System;
using System.Threading.Tasks;

namespace Orleans;

public interface ITransactionClient
{
    /// <summary>
    /// Run transaction delegate
    /// </summary>
    /// <param name="transactionOption"></param>
    /// <param name="transactionDelegate"></param>
    /// <returns><see cref="Task"/></returns>
    /// <remarks>Transaction always commit, unless an exception is thrown from the delegate and depending on <paramref name="transactionOption"/></remarks>
    Task RunTransaction(TransactionOption transactionOption, Func<Task> transactionDelegate);

    /// <summary>
    /// Run transaction delegate
    /// </summary>
    /// <param name="transactionOption"></param>
    /// <param name="transactionDelegate"></param>
    /// <returns>True if the transaction should commit</returns>
    Task RunTransaction(TransactionOption transactionOption, Func<Task<bool>> transactionDelegate);

    /// <summary>
    /// Run transaction delegate with exclusive lock option
    /// </summary>
    /// <param name="transactionOption"></param>
    /// <param name="transactionDelegate"></param>
    /// <param name="useExclusiveLock">When <see langword="true"/>, all transactional states accessed during this transaction
    /// will acquire exclusive locks even for read operations, preventing lock upgrade conflicts under high contention.</param>
    /// <returns><see cref="Task"/></returns>
    Task RunTransaction(TransactionOption transactionOption, Func<Task> transactionDelegate, bool useExclusiveLock);

    /// <summary>
    /// Run transaction delegate with exclusive lock option
    /// </summary>
    /// <param name="transactionOption"></param>
    /// <param name="transactionDelegate"></param>
    /// <param name="useExclusiveLock">When <see langword="true"/>, all transactional states accessed during this transaction
    /// will acquire exclusive locks even for read operations, preventing lock upgrade conflicts under high contention.</param>
    /// <returns>True if the transaction should commit</returns>
    Task RunTransaction(TransactionOption transactionOption, Func<Task<bool>> transactionDelegate, bool useExclusiveLock);
}
