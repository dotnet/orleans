using System;
using System.Diagnostics;
using System.Threading.Tasks;

using Orleans.Serialization;

namespace Orleans.Transactions;

public class TransactionalScope : IAsyncDisposable
{
    private bool _resolveTransaction;
    private readonly TransactionInfo _transactionInfo;
    private readonly ITransactionAgent _transactionAgent;
    private readonly TransactionInfo _ambientTransactionInfo;
    private readonly Serializer<OrleansTransactionAbortedException> _serializer;

    internal TransactionalScope(ITransactionAgent transactionAgent, Serializer<OrleansTransactionAbortedException> serializer)
    {
        _serializer = serializer;
        _transactionAgent = transactionAgent;

        // Try to pick up ambient transaction scope
        _ambientTransactionInfo = TransactionContext.GetTransactionInfo();

        // TODO: this should be a configurable parameter
        var transactionTimeout = Debugger.IsAttached ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(10);

        // Start transaction
        // Note - 'StartTransaction' method is really not async, ending with 'return Task.FromResult<TransactionInfo>(...)'
        // Transaction can not be started in the constructor if 'StartTransaction' really gets to be an async method in the future
        _transactionInfo = _transactionAgent.StartTransaction(false, transactionTimeout).Result;

        // Set current transaction context
        TransactionContext.SetTransactionInfo(_transactionInfo);
    }

    /// <summary>
    /// Set transaction commit intent
    /// </summary>
    public void Commit() => _resolveTransaction = true;

    /// <summary>
    /// Dispose transaction scope
    /// </summary>
    /// <returns></returns>
    /// <remarks>
    /// Transaction will be aborted if there is no commit intent pending, see <see cref="Commit"/>
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        // Set last known transaction context
        TransactionContext.SetTransactionInfo(_ambientTransactionInfo);

        // Tell garbage collector that we're done
        GC.SuppressFinalize(this);

        // Housekeeping
        return HandlePendingTransactions();
    }

    private async ValueTask HandlePendingTransactions()
    {
        // Gather pending transactions
        _transactionInfo.ReconcilePending();

        if (!_resolveTransaction)
        {   // User didn't commit transaction - abort the transaction
            await _transactionAgent.Abort(_transactionInfo);
        }
        else
        {
            // Prepare for exception, if any
            OrleansTransactionException transactionException;

            // Check if transaction is pending for abortion
            transactionException = _transactionInfo.MustAbort(_serializer);

            if (transactionException is not null)
            {   // Transaction is pending for abortion - abort the transaction
                await _transactionAgent.Abort(_transactionInfo);
            }
            else
            {   // Try to resolve transaction
                var (status, exception) = await _transactionAgent.Resolve(_transactionInfo);

                if (status != TransactionalStatus.Ok)
                {   // Resolving transaction failed
                    transactionException = status.ConvertToUserException(_transactionInfo.Id, exception);
                }
            }

            if (transactionException != null)
            {   // Transaction failed - buble up to user code
                throw transactionException;
            }
        }
    }
}
