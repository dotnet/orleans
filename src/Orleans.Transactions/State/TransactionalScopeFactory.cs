using System;
using System.Diagnostics;
using System.Threading.Tasks;

using Orleans.Serialization;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions;

public class TransactionalScopeFactory : ITransactionalScopeFactory
{
    private readonly ITransactionAgent _transactionAgent;
    private readonly Serializer<OrleansTransactionAbortedException> _serializer;

    public TransactionalScopeFactory(
        ITransactionAgent transactionAgent,
        Serializer<OrleansTransactionAbortedException> serializer)
    {
        _serializer = serializer;
        _transactionAgent = transactionAgent;
    }

    public async ValueTask CreateScope(Func<ValueTask> transactionScopeFunc) => await CreateScope(readOnly: false, transactionScopeFunc);

    public async ValueTask CreateScope(bool readOnly, Func<ValueTask> transactionScopeFunc)
    {
        if (transactionScopeFunc == null)
        {   // Do nothing if no delegate was given
            return;
        }

        // Try to pick up ambient transaction scope
        var transactionInfo = TransactionContext.GetTransactionInfo();

        if (transactionInfo == null)
        {   // No ambient transaction found

            // TODO: this should be a configurable parameter
            var transactionTimeout = Debugger.IsAttached ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(10);

            // Start transaction
            transactionInfo = await _transactionAgent.StartTransaction(readOnly, transactionTimeout);

            // Run transaction
            var orleansTransactionException = await RunStartedTransaction(transactionInfo, transactionScopeFunc);

            if (orleansTransactionException != null)
            {   // Transaction failed
                throw orleansTransactionException;
            }
        }
        else
        {   // Ambient transaction found (delegate transaction handling) - Invoke user defined delegate
            await transactionScopeFunc.Invoke();
        }
    }

    private async ValueTask<OrleansTransactionException> RunStartedTransaction(TransactionInfo transactionInfo, Func<ValueTask> transactionScopeFunc)
    {
        // Prepare return value
        OrleansTransactionException transactionException = null;

        try
        {   // Set transaction scope (context)
            TransactionContext.SetTransactionInfo(transactionInfo);

            // Invoke user defined delegate
            await transactionScopeFunc.Invoke();

            // Gather pending transactions
            transactionInfo.ReconcilePending();

            // Check if transaction is pending for abortion
            transactionException = transactionInfo.MustAbort(_serializer);

            if (transactionException is not null)
            {   // Transaction is pending for abortion - abort transaction
                await _transactionAgent.Abort(transactionInfo);
            }
            else
            {   // Try to resolve transaction
                var (status, exception) = await _transactionAgent.Resolve(transactionInfo);

                if (status != TransactionalStatus.Ok)
                {   // Resolving transaction failed
                    transactionException = status.ConvertToUserException(transactionInfo.Id, exception);
                }
            }
        }
        catch (Exception exception)
        {   // Exception caught - buble up exception to user and abort transaction
            transactionException = TransactionalStatus.CommitFailure.ConvertToUserException(transactionInfo.Id, exception);
            await _transactionAgent.Abort(transactionInfo);
        }
        finally
        {   // Clear the transaction scope (context)
            TransactionContext.Clear();
        }

        // Return result
        return transactionException;
    }
}
