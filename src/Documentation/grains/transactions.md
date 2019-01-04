---
layout: page
title: Transactions in Orleans 2.0
---

# Orleans Transactions

Orleans supports distributed ACID transactions against persistent grain state.

## Setup

Orleans transactions are opt-in.
A silo must be configured to use transactions.
If it is not, any calls to transactional methods on grains will receive an `OrleansTransactionsDisabledException`.
To enable transactions on a silo, call `UseTransactions()` on the silo host builder.

```csharp
var builder = new SiloHostBuilder().UseTransactions();

```

### Transactional State Storage

To use transactions, the user needs to configure a data store.
To support various data stores with transactions, the storage abstraction `ITransactionalStateStorage` has been introduced.
This abstraction is specific to the needs of transactions, unlike generic grain storage (`IGrainStorage`).
To use transaction-specific storage, the user can configure their silo using any implementation of `ITransactionalStateStorage`, such as Azure (`AddAzureTableTransactionalStateStorage`).

Example:

```csharp

var builder = new SiloHostBuilder()
    .AddAzureTableTransactionalStateStorage("TransactionStore, options =>
    {
        options.ConnectionString = ”YOUR_STORAGE_CONNECTION_STRING”);
    })
    .UseTransactions();

```

For development purposes, if transaction-specific storage is not available for the data store you need, an `IGrainStorage` implementation may be used instead.
For any transactional state that does not have a store configured for it, transactions will attempt to fail over to grain storage using a bridge.
Accessing transactional state via a bridge to grain storage will be less efficient and is not a pattern we intend to support long term, hence the recommendation that this only be used for development purposes.

## Programming Model

### Grain Interfaces

For a grain to support transactions, transactional methods on a grain interface must be marked as being part of a transaction using the “Transaction” attribute.
The attribute needs indicate how the grain call behaves in a transactional environment via the transaction options below:

- `TransactionOption.Create` - Call is transactional and will always create a new transaction context (i.e., it will start a new transaction), even if called within an existing transaction context.
- `TransactionOption.Join` - Call is transactional but can only be called within the context of an existing transaction.
- `TransactionOption.CreateOrJoin` - Call is transactional. If called within the context of a transaction, it will use that context, else it will create a new context.
- `TransactionOption.Suppress` - Call is not transactional but can be called from within a transaction. If called within the context of a transaction, the context will not be passed to the call.
- `TransactionOption.Supported` - Call is not transactional but supports transactions. If called within the context of a transaction, the context will be passed to the call.
- `TransactionOption.NotAllowed` - Call is not transactional and cannot be called from within a transaction. If called within the context of a transaction, it will throw a `NotSupportedException`.

Calls can be marked as “Create”, meaning the call will always start its own transaction.
For example, the Transfer operation in the ATM grain below will always start a new transaction which involves the two referenced accounts.

```csharp

public interface IATMGrain : IGrainWithIntegerKey
{
    [Transaction(TransactionOption.Create)]
    Task Transfer(Guid fromAccount, Guid toAccount, uint amountToTransfer);
}

```

The transactional operations Withdraw and Deposit on the account grain are marked “Join”, indicating that they can only be called within the context of an existing transaction, which would be the case if they were called during `IATMGrain.Transfer(…)`.
The `GetBalance` call is marked `CreateOrJoin` so it can be called from within an existing transaction, like via `IATMGrain.Transfer(…)`, or on its own.

```csharp

public interface IAccountGrain : IGrainWithGuidKey
{
    [Transaction(TransactionOption.Join)]
    Task Withdraw(uint amount);

    [Transaction(TransactionOption.Join)]
    Task Deposit(uint amount);

    [Transaction(TransactionOption.CreateOrJoin)]
    Task<uint> GetBalance();
}

```

### Grain Implementations

A grain implementation needs to use an `ITransactionalState` facet (see Facet System) to manage grain state via ACID transactions.

```csharp

    public interface ITransactionalState<TState>  
        where TState : class, new()
    {
        Task<TResult> PerformRead<TResult>(Func<TState, TResult> readFunction);
        Task<TResult> PerformUpdate<TResult>(Func<TState, TResult> updateFunction);
    }

```

All read or write access to the persisted state must be performed via synchronous functions passed to the transactional state facet.
This allows the transaction system to perform or cancel these operations transactionally.
To use a transactional state within a grain, one only needs to define a serializable state class to be persisted and to declare the transactional state in the grain’s constructor with a `TransactionalState` attribute. The latter declares the state name and (optionally) which transactional state storage to use (see Setup).

```csharp

[AttributeUsage(AttributeTargets.Parameter)]
public class TransactionalStateAttribute : Attribute
{
    public TransactionalStateAttribute(string stateName, string storageName = null)
    {
      …
    }
}

```

Example:

```csharp

public class AccountGrain : Grain, IAccountGrain
{
    private readonly ITransactionalState<Balance> balance;

    public AccountGrain(
        [TransactionalState("balance", "TransactionStore")]
        ITransactionalState<Balance> balance)
    {
        this.balance = balance ?? throw new ArgumentNullException(nameof(balance));
    }

    Task IAccountGrain.Deposit(uint amount)
    {
        return this.balance.PerformUpdate(x => x.Value += amount);
    }

    Task IAccountGrain.Withdrawal(uint amount)
    {
        return this.balance.PerformUpdate(x => x.Value -= amount);
    }

    Task<uint> IAccountGrain.GetBalance()
    {
        return this.balance.PerformRead(x => x.Value);
    }
}

```

In the above example, the attribute `TransactionalState` is used to declare that the ‘balance’ constructor argument should be associated with a transactional state named “balance”.
With this declaration, Orleans will inject an `ITransactionalState` instance with a state loaded from the transactional state storage named "TransactionStore" (see Setup).
The state can be modified via `PerformUpdate` or read via `PerformRead`.
The transaction infrastructure will ensure that any such changes performed as part of a transaction, even among multiple grains distributed over an Orleans cluster, will either all be committed or all be undone upon completion of the grain call that created the transaction (`IATMGrain.Transfer` in the above examples).

### Calling Transactions

Transactional methods on a grain interface are called like any other grain call.

```csharp

    IATMGrain atm = client.GetGrain<IATMGrain>(0);
    Guid from = Guid.NewGuid();
    Guid to = Guid.NewGuid();
    await atm.Transfer(from, to, 100);
    uint fromBalance = await client.GetGrain<IAccountGrain>(from).GetBalance();
    uint toBalance = await client.GetGrain<IAccountGrain>(to).GetBalance();

```

In the above calls, an ATM grain is used to transfer 100 units of currency from one account to another.
After the transfer is complete, both accounts are queried to get their current balance.
The currency transfer as well as both account queries are performed as ACID transactions.

As seen in the above example, transactions can return values within a task, like other grain calls, but upon call failure they will not throw application exceptions, but rather an `OrleansTransactionException` or `TimeoutException`.
If the application throws an exception during the transaction and that exception causes the transaction to fail (as opposed failing because of other system failures), the application exception will be the inner exception of the `OrleansTransactionException`.
If a transaction exception is thrown of type `OrleansTransactionAbortedException`, the transaction failed and can be retried.
Any other exception thrown indicates that the transaction terminated with an unknown state.
Since transactions are distributed operations, a transaction in an unknown state could have succeeded, failed, or still be in progress.
For this reason, it’s advisable to allow a call timeout period (`SiloMessagingOptions.ResponseTimeout`) to pass, to avoid cascading aborts, before verifying the state or retrying the operation.
