using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

using Orleans.Serialization;

namespace Orleans.Transactions;

internal class TransactionClient : ITransactionClient
{
    private readonly ITransactionAgent _transactionAgent;
    private readonly Serializer<OrleansTransactionAbortedException> _serializer;

    public TransactionClient(ITransactionAgent transactionAgent, Serializer<OrleansTransactionAbortedException> serializer)
    {
        _transactionAgent = transactionAgent;
        _serializer = serializer;
    }

    public async Task RunTransaction(TransactionOption transactionOption, Func<Task> transactionDelegate)
    {
        if (transactionDelegate is null)
        {
            throw new ArgumentNullException(nameof(transactionDelegate));
        }

        await RunTransaction(transactionOption, async () =>
        {
            await transactionDelegate();
            return true;
        });
    }

    public async Task RunTransaction(TransactionOption transactionOption, Func<Task<bool>> transactionDelegate)
    {
        if (transactionDelegate is null)
        {
            throw new ArgumentNullException(nameof(transactionDelegate));
        }

        // Pick up ambient transaction context
        var ambientTransactionInfo = TransactionContext.GetTransactionInfo();

        if (ambientTransactionInfo is not null && transactionOption == TransactionOption.Suppress)
        {
            throw new NotSupportedException("Delegate cannot be executed within a transaction.");
        }

        if (ambientTransactionInfo is null && transactionOption == TransactionOption.Join)
        {
            throw new NotSupportedException("Delegate cannot be executed outside of a transaction.");
        }

        try
        {
            switch (transactionOption)
            {
                case TransactionOption.Create:
                    await RunDelegateWithTransaction(null, transactionDelegate);
                    break;
                case TransactionOption.Join:
                    await RunDelegateWithTransaction(ambientTransactionInfo, transactionDelegate);
                    break;
                case TransactionOption.CreateOrJoin:
                    await RunDelegateWithTransaction(ambientTransactionInfo, transactionDelegate);
                    break;
                case TransactionOption.Suppress:
                    await RunDelegateWithSupressedTransaction(ambientTransactionInfo, transactionDelegate);
                    break;
                case TransactionOption.Supported:
                    await RunDelegateWithSupportedTransaction(ambientTransactionInfo, transactionDelegate);
                    break;
                case TransactionOption.NotAllowed:
                    await RunDelegateWithDisallowedTransaction(ambientTransactionInfo, transactionDelegate);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(transactionOption), $"{transactionOption} is not supported");
            }
        }
        finally
        {
            // Restore ambient transaction context, if any
            TransactionContext.SetTransactionInfo(ambientTransactionInfo);
        }
    }

    private static async Task RunDelegateWithDisallowedTransaction(TransactionInfo ambientTransactionInfo, Func<Task<bool>> transactionDelegate)
    {
        if (ambientTransactionInfo is not null)
        {
            // No transaction is allowed within delegate
            throw new NotSupportedException("Delegate cannot be executed within a transaction.");
        }

        // Run delegate
        _ = await transactionDelegate();
    }

    private static async Task RunDelegateWithSupportedTransaction(TransactionInfo ambientTransactionInfo, Func<Task<bool>> transactionDelegate)
    {
        if (ambientTransactionInfo is null)
        {
            // Run delegate
            _ = await transactionDelegate();
        }
        else
        {
            // Run delegate
            ambientTransactionInfo.TryToCommit = await transactionDelegate();
        }
    }

    private static async Task RunDelegateWithSupressedTransaction(TransactionInfo ambientTransactionInfo, Func<Task<bool>> transactionDelegate)
    {
        // Clear transaction context
        TransactionContext.Clear();

        if (ambientTransactionInfo is null)
        {
            // Run delegate
            _ = await transactionDelegate();
        }
        else
        {
            // Run delegate
            ambientTransactionInfo.TryToCommit = await transactionDelegate();
        }
    }

    private async Task RunDelegateWithTransaction(TransactionInfo ambientTransactionInfo, Func<Task<bool>> transactionDelegate)
    {
        TransactionInfo transactionInfo;

        if (ambientTransactionInfo is null)
        {
            // TODO: this should be a configurable parameter
            var transactionTimeout = Debugger.IsAttached ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(10);

            // Start transaction
            transactionInfo = await _transactionAgent.StartTransaction(readOnly: false, transactionTimeout);
        }
        else
        {
            // Fork ambient transaction
            transactionInfo = ambientTransactionInfo.Fork();
        }

        // Set transaction context
        TransactionContext.SetTransactionInfo(transactionInfo);

        try
        {
            // Run delegate
            transactionInfo.TryToCommit = await transactionDelegate();
        }
        catch (Exception exception)
        {
            // Record exception with transaction
            transactionInfo.RecordException(exception, _serializer);
        }

        // Gather pending actions into transaction
        transactionInfo.ReconcilePending();

        if (ambientTransactionInfo is null)
        {
            // Finalize transaction since there is no ambient transaction to join
            await FinalizeTransaction(transactionInfo);
        }
        else
        {   // Join transaction with ambient transaction
            ambientTransactionInfo.Join(transactionInfo);
        }
    }

    private async Task FinalizeTransaction(TransactionInfo transactionInfo)
    {
        // Prepare for exception, if any
        OrleansTransactionException transactionException;

        // Check if transaction is pending for abort
        transactionException = transactionInfo.MustAbort(_serializer);

        if (transactionException is not null || transactionInfo.TryToCommit is false)
        {
            // Transaction is pending for abort
            await _transactionAgent.Abort(transactionInfo);
        }
        else
        {
            // Try to resolve transaction
            var (status, exception) = await _transactionAgent.Resolve(transactionInfo);

            if (status != TransactionalStatus.Ok)
            {
                // Resolving transaction failed
                transactionException = status.ConvertToUserException(transactionInfo.Id, exception);
                ExceptionDispatchInfo.SetCurrentStackTrace(transactionException);
            }
        }

        if (transactionException != null)
        {
            // Transaction failed - bubble up exception
            ExceptionDispatchInfo.Throw(transactionException);
        }
    }
}
