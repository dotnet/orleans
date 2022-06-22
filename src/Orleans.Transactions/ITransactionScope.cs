using System.Threading.Tasks;
using System;

namespace Orleans;

public interface ITransactionScope
{
    /// <summary>
    /// Run transaction scope
    /// </summary>
    /// <param name="transactionOption"></param>
    /// <param name="transactionScope"></param>
    /// <returns></returns>
    Task RunScope(TransactionOption transactionOption, Func<Task> transactionScope);
}
