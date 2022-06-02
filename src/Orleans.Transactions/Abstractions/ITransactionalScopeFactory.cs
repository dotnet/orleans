using System;
using System.Threading.Tasks;

namespace Orleans.Transactions.Abstractions;

/// <summary>
/// Factory to create user transactional scopes
/// </summary>
public interface ITransactionalScopeFactory
{
    /// <summary>
    /// Create transaction scope and invoke user delegate
    /// </summary>
    /// <param name="transactionScopeFunc"></param>
    /// <returns></returns>
    /// <exception cref="OrleansTransactionException"/>
    /// <remarks>
    /// Delegate is ignored if <paramref name="transactionScopeFunc"/> is null
    /// </remarks>
    ValueTask CreateScope(Func<ValueTask> transactionScopeFunc);

    /// <summary>
    /// Create transaction scope and invoke user delegate
    /// </summary>
    /// <param name="readOnly"></param>
    /// <param name="transactionScopeFunc"></param>
    /// <returns></returns>
    /// <exception cref="OrleansTransactionException"/>
    /// <remarks>
    /// Delegate is ignored if <paramref name="transactionScopeFunc"/> is null
    /// </remarks>
    ValueTask CreateScope(bool readOnly, Func<ValueTask> transactionScopeFunc);
}
