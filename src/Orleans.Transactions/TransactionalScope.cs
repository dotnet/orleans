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
    private static readonly ValueTask CompletedValueTask = new();
    private readonly Serializer<OrleansTransactionAbortedException> _serializer;
    private const string NoCommitExceptionMessage = "Missing transaction commit detected";

    internal TransactionalScope(ITransactionAgent transactionAgent, Serializer<OrleansTransactionAbortedException> serializer)
    {
        _serializer = serializer;
        _transactionAgent = transactionAgent;

        // Pick up ambient transaction scope
        _ambientTransactionInfo = TransactionContext.GetTransactionInfo();

        if (_ambientTransactionInfo == null)
        {
            // TODO: this should be a configurable parameter
            var transactionTimeout = Debugger.IsAttached ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(10);

            // Start transaction
            _transactionInfo = _transactionAgent.StartTransaction(false, transactionTimeout).Result;
        }
        else
        {   // Fork ambient transaction
            _transactionInfo = _ambientTransactionInfo.Fork();
        }

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
        if (_ambientTransactionInfo != null)
        {
            if (!_resolveTransaction && _transactionInfo.OriginalException == null)
            {   // User didn't commit transaction
                _transactionInfo.RecordException(new OrleansTransactionAbortedException(_transactionInfo.Id, NoCommitExceptionMessage), _serializer);
            }

            // Gather pending transactions
            _transactionInfo.ReconcilePending();

            // Join into ambient transaction
            _ambientTransactionInfo.Join(_transactionInfo);

            // Set last known transaction context
            TransactionContext.SetTransactionInfo(_ambientTransactionInfo);
        }
        else
        {
            // Clear transaction context
            TransactionContext.Clear();
        }

        // Tell garbage collector that we're done
        GC.SuppressFinalize(this);

        return _ambientTransactionInfo == null
            ? ResolveTransaction()
            : CompletedValueTask;
    }

    private async ValueTask ResolveTransaction()
    {
        // Gather pending transactions
        _transactionInfo.ReconcilePending();

        if (!_resolveTransaction && _transactionInfo.OriginalException == null)
        {   // User didn't commit transaction
            _transactionInfo.RecordException(new OrleansTransactionAbortedException(_transactionInfo.Id, NoCommitExceptionMessage), _serializer);
        }

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
