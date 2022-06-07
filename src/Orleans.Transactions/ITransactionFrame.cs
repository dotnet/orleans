using System.Threading.Tasks;
using System;

namespace Orleans.Transactions;

public interface ITransactionFrame
{
    /// <summary>
    /// Run transaction scope
    /// </summary>
    /// <param name="transactionOption"></param>
    /// <param name="transactionScope"></param>
    /// <returns></returns>
    Task RunScope(TransactionOption transactionOption, Func<Task> transactionScope);
}
