using System;
using System.Diagnostics;
using System.Threading.Tasks;

using Orleans.Serialization;

namespace Orleans.Transactions;

internal class TransactionScope : ITransactionScope
{
    private readonly ITransactionAgent _transactionAgent;
    private readonly Serializer<OrleansTransactionAbortedException> _serializer;

    public TransactionScope(ITransactionAgent transactionAgent, Serializer<OrleansTransactionAbortedException> serializer)
    {
        _transactionAgent = transactionAgent;
        _serializer = serializer;
    }

    public async Task RunScope(TransactionOption transactionOption, Func<Task> transactionScope)
    {
        if (transactionScope is null)
        {
            throw new ArgumentNullException(nameof(transactionScope));
        }

        // Pick up ambient transaction context
        var ambientTransactionInfo = TransactionContext.GetTransactionInfo();

        if (ambientTransactionInfo is not null && transactionOption == TransactionOption.Suppress)
        {
            throw new NotSupportedException("Scope cannot be executed within a transaction.");
        }

        if (ambientTransactionInfo is null && transactionOption == TransactionOption.Join)
        {
            throw new NotSupportedException("Scope cannot be executed outside of a transaction.");
        }

        try
        {
            switch (transactionOption)
            {
                case TransactionOption.Create:
                    await RunScopeWithTransaction(null, transactionScope);
                    break;
                case TransactionOption.Join:
                    await RunScopeWithTransaction(ambientTransactionInfo, transactionScope);
                    break;
                case TransactionOption.CreateOrJoin:
                    await RunScopeWithTransaction(ambientTransactionInfo, transactionScope);
                    break;
                case TransactionOption.Suppress:
                    await RunScopeWithSupressedTransaction(transactionScope);
                    break;
                case TransactionOption.Supported:
                    await RunScopedWithSupportedTransaction(transactionScope);
                    break;
                case TransactionOption.NotAllowed:
                    await RunScopeWithDisallowedTransaction(ambientTransactionInfo, transactionScope);
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

    private static async Task RunScopeWithDisallowedTransaction(TransactionInfo ambientTransactionInfo, Func<Task> transactionScope)
    {
        if (ambientTransactionInfo is not null)
        {   // No transaction is allowed within scope
            throw new NotSupportedException("Scope cannot be executed within a transaction.");
        }

        // Run scope
        await transactionScope.Invoke();
    }

    private static async Task RunScopedWithSupportedTransaction(Func<Task> transactionScope) =>
        // Run scope
        await transactionScope.Invoke();

    private static async Task RunScopeWithSupressedTransaction(Func<Task> transactionScope)
    {
        // Clear transaction context
        TransactionContext.Clear();

        // Run scope
        await transactionScope.Invoke();
    }

    private async Task RunScopeWithTransaction(TransactionInfo ambientTransactionInfo, Func<Task> transactionScope)
    {
        TransactionInfo transactionInfo;

        if (ambientTransactionInfo is null)
        {
            // TODO: this should be a configurable parameter
            var transactionTimeout = Debugger.IsAttached ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(10);

            // Start transaction
            transactionInfo = await _transactionAgent.StartTransaction(false, transactionTimeout);
        }
        else
        {   // Fork ambient transaction
            transactionInfo = ambientTransactionInfo.Fork();
        }

        // Set transaction context
        TransactionContext.SetTransactionInfo(transactionInfo);

        try
        {   // Run scope
            await transactionScope.Invoke();
        }
        catch (Exception exception)
        {   // Record exception with transaction
            transactionInfo.RecordException(exception, _serializer);
        }

        // Gather pending actions into transaction
        transactionInfo.ReconcilePending();

        if (ambientTransactionInfo is null)
        {   // Finalize transaction since there is no ambient transaction to join
            await FinalizeTransaction(transactionInfo);
        }
        else
        {
            // Join transaction into ambient transaction
            ambientTransactionInfo.Join(transactionInfo);
        }
    }

    private async Task FinalizeTransaction(TransactionInfo transactionInfo)
    {
        // Prepare for exception, if any
        OrleansTransactionException transactionException;

        // Check if transaction is pending for abort
        transactionException = transactionInfo.MustAbort(_serializer);

        if (transactionException is not null)
        {   // Transaction is pending for abort
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

        if (transactionException != null)
        {   // Transaction failed - bubble up exception
            throw transactionException;
        }
    }
}
