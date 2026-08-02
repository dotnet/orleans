---
title: Transactions in Orleans
description: Use distributed ACID transactions with Orleans transactional state.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Orleans transactions

The `Microsoft.Orleans.Transactions` package provides distributed ACID transactions across one or more grain calls and transactional state records. Transactional state is distinct from <xref:Orleans.Runtime.IPersistentState`1>: use <xref:Orleans.Transactions.Abstractions.ITransactionalState`1> for data that participates in an Orleans transaction.

## Enable transactions

Enable transactions on every participating silo:

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleans(siloBuilder =>
{
    siloBuilder.UseTransactions();
});
```

External Orleans clients that create or propagate transactions must also call `UseTransactions`:

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

If no named transactional provider exists, Orleans can bridge transactional state to a configured `IGrainStorage`. The bridge is less efficient and is intended for development and compatibility scenarios. Prefer a transactional provider for production workloads.

## Declare transaction behavior

Apply <xref:Orleans.TransactionAttribute> to grain interface methods:

| Option | Behavior |
|---|---|
| `Create` | Always starts a new transaction and suppresses any ambient transaction for this call. |
| `CreateOrJoin` | Joins the ambient transaction or starts one when none exists. |
| `Join` | Requires an ambient transaction. |
| `Suppress` | Executes without the ambient transaction. |
| `Supported` | Receives an ambient transaction when one exists but doesn't require one. |
| `NotAllowed` | Fails when called in a transaction. |

:::code source="snippets/transactions/Abstractions/IAccountGrain.cs":::

`Join` outside a transaction and `NotAllowed` inside a transaction throw <xref:System.NotSupportedException>.

### Read-only transactions

Apply <xref:Orleans.Concurrency.ReadOnlyAttribute> to a transactional method that performs no transactional-state update:

```csharp
[ReadOnly]
[Transaction(TransactionOption.CreateOrJoin)]
Task<uint> GetBalance();
```

Orleans starts a read-only transaction and can use a reduced commit path. A write attempted by a read-only transaction aborts with <xref:Orleans.Transactions.OrleansReadOnlyViolatedException>. The `TransactionAttribute.ReadOnly` property is obsolete; use `[ReadOnly]`.

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

Read through `PerformRead` and update through `PerformUpdate`. The delegates are synchronous because Orleans controls when the state snapshot is read, committed, or discarded. Don't retain the supplied state object or mutate it outside these delegates.

> [!NOTE]
> A transactional grain doesn't need `[Reentrant]`. Reentrancy is an independent scheduling choice. Enable it only when the grain implementation is safe for interleaved calls and the throughput benefit is understood.

Transactional state isn't available during `OnActivateAsync`; transaction setup occurs as part of a transactional request.

## Start a transaction

### From a grain call

A method marked `Create` or `CreateOrJoin` starts a transaction when no ambient transaction exists. Calls made from that method propagate the context according to each target method's `TransactionOption`.

:::code source="snippets/transactions/Grains/AtmGrain.cs":::

### From an external client

Resolve <xref:Orleans.ITransactionClient> and run a delegate:

:::code source="snippets/transactions/Client/Program.cs" highlight="11-12,30-31,38-44":::

`RunTransaction` has delegates returning `Task` and `Task<bool>`. The `Task<bool>` form commits only when the delegate returns `true`; returning `false` aborts. An overload also accepts `useExclusiveLock`.

## Commit and abort semantics

The request that starts a transaction resolves it before returning:

- A successful delegate commits unless it explicitly returns `false`.
- An application exception records the failure and aborts the transaction.
- An abort discards all transactional-state updates in that transaction.
- The original application exception can appear as the inner exception of an `OrleansTransactionException`.

An <xref:Orleans.Transactions.OrleansTransactionAbortedException> reports a known abort and can be retried if the application command is safe to retry. <xref:Orleans.Transactions.OrleansTransactionInDoubtException> means the coordinator couldn't determine the final outcome. Don't immediately repeat a non-idempotent command after an in-doubt result; use an operation identifier and query application state after the response-timeout window.

Retries must retry the entire transaction, not an individual participant update. Bound attempts and use backoff. High contention, lock-upgrade conflicts, overload, and storage outages can otherwise create a retry storm.

## Contention and timeouts

Transactional state uses reader/writer locks and deadlock-prevention rules. Common abort causes include:

- Failure to acquire a lock before `LockAcquireTimeout`.
- A lock held longer than `LockTimeout`.
- A lock-upgrade conflict.
- Failure to complete prepare before `PrepareTimeout`.
- A participant or transaction service becoming unavailable.

The default <xref:Orleans.Configuration.TransactionalStateOptions> values are:

| Option | Default |
|---|---|
| `LockTimeout` | 8 seconds |
| `LockAcquireTimeout` | 10 seconds |
| `PrepareTimeout` | 20 seconds |
| `RemoteTransactionPingFrequency` | 60 seconds |
| `ConfirmationRetryDelay` | 30 seconds |
| `MaxLockGroupSize` | 20 |

Commit confirmation has a retry limit of 3. A newly started transaction currently uses a 10-second transaction timeout when no debugger is attached.

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
- Use `[ReadOnly]` for truly read-only operations.
- Use `[UseExclusiveLock]` selectively when lock upgrades are a measured source of aborts.
- Make the initiating command idempotent and include an operation identifier.
- Monitor aborts, in-doubt outcomes, lock timeouts, prepare timeouts, and storage latency separately.
