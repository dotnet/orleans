---
title: External tasks and grain scheduling
description: Safely use asynchronous libraries, CPU work, and blocking APIs from Orleans grains.
ms.date: 08/02/2026
ms.topic: concept-article
---

# External tasks and grain scheduling

Normal `await` keeps grain code in the activation's turn-based scheduling model. The continuation resumes on the grain scheduler, so it can safely access grain state:

```csharp
public async Task Refresh()
{
    Item value = await repository.Load();
    _cachedItem = value;
}
```

Async libraries don't need `Task.Run`. Await them directly.

## Don't block the grain scheduler

Never wait synchronously on incomplete tasks using `.Result`, `.Wait()`, `WaitAll`, or `GetAwaiter().GetResult()`. Blocking can deadlock the activation and starve the .NET thread pool.

Avoid `async void`, including async lambdas passed to APIs expecting `Action<T>`. Exceptions can't be observed through a returned task and can terminate the process.

## Use Task.Run narrowly

<xref:System.Threading.Tasks.Task.Run*> executes on the .NET thread pool outside the Orleans scheduler. Use it only for:

- An unavoidable synchronous blocking API.
- CPU-heavy work that doesn't access grain state.

Capture immutable input, do the external work, then update grain state after awaiting:

```csharp
public async Task<int> Compress(byte[] input)
{
    byte[] copy = input.ToArray();

    int size = await Task.Run(
        () => CompressSynchronously(copy));

    _lastCompressedSize = size;
    return size;
}
```

Don't read or mutate grain fields inside the `Task.Run` delegate. That code isn't protected by the grain scheduler.

## ConfigureAwait

Don't use `ConfigureAwait(false)` directly in grain methods. It can resume the continuation outside the activation scheduler. General-purpose libraries can use `ConfigureAwait(false)` internally; grain code returns to its scheduler when it normally awaits the library's task.

## Start activation-scheduled work

`Task.Factory.StartNew` without an explicit scheduler uses `TaskScheduler.Current`, which is the Orleans scheduler in grain code. This is rarely needed; ordinary async methods are clearer.

If an advanced integration passes an async delegate to `Task.Factory.StartNew`, unwrap the nested task:

```csharp
Task work = Task.Factory
    .StartNew(WorkerAsync)
    .Unwrap();

await work;
```

Don't start unobserved background work that outlives the request. Use [grain timers](timers-and-reminders.md), reminders, a durable job abstraction, or a hosted service depending on the required lifetime and reliability.

## Reentrancy still applies

Awaiting external work doesn't let another request run on a non-reentrant grain. Marking a grain reentrant or enabling interleaving can improve throughput while calls wait for I/O, but state can change between turns. See [Request scheduling](request-scheduling.md).
