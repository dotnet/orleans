---
title: Transactions in Orleans
description: Use distributed ACID transactions with Orleans transactional state.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Orleans transactions

The [`Microsoft.Orleans.Transactions`](https://www.nuget.org/packages/Microsoft.Orleans.Transactions) package provides distributed ACID transactions across one or more grain calls and transactional state records. Transactional state is distinct from <xref:Orleans.Runtime.IPersistentState`1>: use <xref:Orleans.Transactions.Abstractions.ITransactionalState`1> for data that participates in an Orleans transaction.

## Enable transactions

Enable transactions on every participating silo with <xref:Orleans.Hosting.SiloBuilderExtensions.UseTransactions*>:

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleans(siloBuilder =>
{
    siloBuilder.UseTransactions();
});
```

External Orleans clients that create or propagate transactions must also call <xref:Orleans.Hosting.ClientBuilderExtensions.UseTransactions*>:

```csharp
builder.UseOrleansClient(clientBuilder =>
{
    clientBuilder.UseTransactions();
});
```

A transactional call without transaction services fails with <xref:Orleans.Transactions.OrleansTransactionsDisabledException>.

## Configure transactional storage

Transactional storage implements <xref:Orleans.Transactions.Abstractions.ITransactionalStateStorage`1>. The supported provider package `Microsoft.Orleans.Transactions.AzureStorage` registers Azure Table transactional storage:

```csharp
siloBuilder
    .AddAzureTableTransactionalStateStorage(
        "TransactionStore",
        options =>
        {
            options.ConfigureTableServiceClient(
                builder.Configuration.GetConnectionString("transactions"));
        })
    .UseTransactions();
```

If no named transactional provider exists, Orleans can bridge transactional state to a configured <xref:Orleans.Storage.IGrainStorage>. The bridge is less efficient and is intended for development and compatibility scenarios. Prefer a transactional provider for production workloads.

## Declare transaction behavior

Apply <xref:Orleans.TransactionAttribute> to grain interface methods:

| Option | Behavior |
|---|---|
| <xref:Orleans.TransactionOption.Create> | Always starts a new transaction and suppresses any ambient transaction for this call. |
| <xref:Orleans.TransactionOption.CreateOrJoin> | Joins the ambient transaction or starts one when none exists. |
| <xref:Orleans.TransactionOption.Join> | Requires an ambient transaction. |
| <xref:Orleans.TransactionOption.Suppress> | Executes without the ambient transaction. |
| <xref:Orleans.TransactionOption.Supported> | Receives an ambient transaction when one exists but doesn't require one. |
| <xref:Orleans.TransactionOption.NotAllowed> | Fails when called in a transaction. |

:::code source="snippets/transactions/Abstractions/IAccountGrain.cs":::

<xref:Orleans.TransactionOption.Join> outside a transaction and <xref:Orleans.TransactionOption.NotAllowed> inside a transaction throw <xref:System.NotSupportedException>.

### Read-only transactions

Apply <xref:Orleans.Concurrency.ReadOnlyAttribute> to a transactional method that performs no transactional-state update:

```csharp
[ReadOnly]
[Transaction(TransactionOption.CreateOrJoin)]
Task<uint> GetBalance();
```

Orleans starts a read-only transaction and can use a reduced commit path. A write attempted by a read-only transaction aborts with <xref:Orleans.Transactions.OrleansReadOnlyViolatedException>. The <xref:Orleans.TransactionAttribute.ReadOnly> property is obsolete; use <xref:Orleans.Concurrency.ReadOnlyAttribute>.

### Exclusive locks

Transactional state normally permits concurrent readers and upgrades to an exclusive lock when a transaction writes. Competing upgrades can abort under contention. Apply <xref:Orleans.UseExclusiveLockAttribute> to acquire exclusive locks even for reads:

```csharp
[UseExclusiveLock]
[Transaction(TransactionOption.CreateOrJoin)]
Task<uint> ReserveAndGetBalance();
```

This avoids lock-upgrade conflicts at the cost of lower read concurrency. Use it for methods likely to write after reading or for measured contention hot spots, not as a blanket default.

## Access transactional state

Inject a named state facet using <xref:Orleans.Transactions.Abstractions.TransactionalStateAttribute>:

:::code source="snippets/transactions/Grains/AccountGrain.cs":::

Read through <xref:Orleans.Transactions.Abstractions.ITransactionalState`1.PerformRead*> and update through <xref:Orleans.Transactions.Abstractions.ITransactionalState`1.PerformUpdate*>. The delegates are synchronous because Orleans controls when the state snapshot is read, committed, or discarded. Don't retain the supplied state object or mutate it outside these delegates.

> [!NOTE]
> A transactional grain doesn't need <xref:Orleans.Concurrency.ReentrantAttribute>. Reentrancy is an independent scheduling choice. Enable it only when the grain implementation is safe for interleaved calls and the throughput benefit is understood.

Transactional state isn't available during <xref:Orleans.Grain.OnActivateAsync*>; transaction setup occurs as part of a transactional request.

## Start a transaction

### From a grain call

A method marked <xref:Orleans.TransactionOption.Create> or <xref:Orleans.TransactionOption.CreateOrJoin> starts a transaction when no ambient transaction exists. Calls made from that method propagate the context according to each target method's <xref:Orleans.TransactionOption>.

:::code source="snippets/transactions/Grains/AtmGrain.cs":::

### From an external client

Resolve <xref:Orleans.ITransactionClient> and run a delegate:

:::code source="snippets/transactions/Client/Program.cs" highlight="11-12,30-31,38-44":::

<xref:Orleans.ITransactionClient.RunTransaction*> has delegates returning <xref:System.Threading.Tasks.Task> and <xref:System.Threading.Tasks.Task`1>. The generic task form commits only when the delegate returns `true`; returning `false` aborts. An overload also accepts `useExclusiveLock`.

## Commit and abort semantics

The request that starts a transaction resolves it before returning:

- A successful delegate commits unless it explicitly returns `false`.
- An application exception records the failure and aborts the transaction.
- An abort discards all transactional-state updates in that transaction.
- The original application exception can appear as the inner exception of an <xref:Orleans.Transactions.OrleansTransactionException>.

An <xref:Orleans.Transactions.OrleansTransactionAbortedException> reports a known abort and can be retried if the application command is safe to retry. <xref:Orleans.Transactions.OrleansTransactionInDoubtException> means the coordinator couldn't determine the final outcome. Don't immediately repeat a non-idempotent command after an in-doubt result; use an operation identifier and query application state after the response-timeout window.

Retries must retry the entire transaction, not an individual participant update. Bound attempts and use backoff. High contention, lock-upgrade conflicts, overload, and storage outages can otherwise create a retry storm.

## Contention and timeouts

Transactional state uses reader/writer locks and deadlock-prevention rules. Common abort causes include:

- Failure to acquire a lock before <xref:Orleans.Configuration.TransactionalStateOptions.LockAcquireTimeout>.
- A lock held longer than <xref:Orleans.Configuration.TransactionalStateOptions.LockTimeout>.
- A lock-upgrade conflict.
- Failure to complete prepare before <xref:Orleans.Configuration.TransactionalStateOptions.PrepareTimeout>.
- A participant or transaction service becoming unavailable.

The default <xref:Orleans.Configuration.TransactionalStateOptions> values are:

| Option | Default |
|---|---|
| <xref:Orleans.Configuration.TransactionalStateOptions.LockTimeout> | 8 seconds |
| <xref:Orleans.Configuration.TransactionalStateOptions.LockAcquireTimeout> | 10 seconds |
| <xref:Orleans.Configuration.TransactionalStateOptions.PrepareTimeout> | 20 seconds |
| <xref:Orleans.Configuration.TransactionalStateOptions.RemoteTransactionPingFrequency> | 60 seconds |
| <xref:Orleans.Configuration.TransactionalStateOptions.ConfirmationRetryDelay> | 30 seconds |
| <xref:Orleans.Configuration.TransactionalStateOptions.MaxLockGroupSize> | 20 |

Commit confirmation uses <xref:Orleans.Configuration.TransactionalStateOptions.ConfirmationRetryLimit>, whose default is 3. A newly started transaction uses a 10-second transaction timeout when no debugger is attached.

Configure state options consistently on participating silos:

```csharp
siloBuilder.Configure<TransactionalStateOptions>(options =>
{
    options.LockAcquireTimeout = TimeSpan.FromSeconds(5);
    options.LockTimeout = TimeSpan.FromSeconds(8);
    options.PrepareTimeout = TimeSpan.FromSeconds(20);
});
```

Shorter timeouts fail faster but can abort healthy work during load spikes. Longer timeouts retain locks and resources longer. Measure transaction duration and contention before changing defaults.

## Design guidance

- Keep transactions short and avoid unrelated remote calls while holding transactional locks.
- Acquire resources in a stable application-level order when practical.
- Use <xref:Orleans.Concurrency.ReadOnlyAttribute> for truly read-only operations.
- Use <xref:Orleans.UseExclusiveLockAttribute> selectively when lock upgrades are a measured source of aborts.
- Make the initiating command idempotent and include an operation identifier.
- Monitor aborts, in-doubt outcomes, lock timeouts, prepare timeouts, and storage latency separately.
