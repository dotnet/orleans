---
title: External tasks and grain scheduling
description: Safely use asynchronous libraries, CPU work, and blocking APIs from Orleans grains.
ms.date: 08/21/2026
ms.topic: concept-article
---

# External tasks and grain scheduling

Normal `await` keeps grain code in the activation's turn-based scheduling model. The continuation resumes on the grain scheduler, so it can safely access grain state:

:::code language="csharp" source="../snippets/compiled/Grains/GeneralSnippets.cs" id="refresh_from_repository":::
Async libraries don't need <xref:System.Threading.Tasks.Task.Run*>. Await them directly.

## Don't block the grain scheduler

Never wait synchronously on incomplete tasks using <xref:System.Threading.Tasks.Task`1.Result>, <xref:System.Threading.Tasks.Task.Wait*>, <xref:System.Threading.Tasks.Task.WaitAll*>, or `GetAwaiter().GetResult()`. Blocking can deadlock the activation and starve the .NET thread pool.

Avoid `async void`, including async lambdas passed to APIs expecting <xref:System.Action`1>. Exceptions can't be observed through a returned task and can terminate the process.

## Use Task.Run narrowly

<xref:System.Threading.Tasks.Task.Run*> executes on the .NET thread pool outside the Orleans scheduler. Use it only for:

- An unavoidable synchronous blocking API.
- CPU-heavy work that doesn't access grain state.

Capture immutable input, do the external work, then update grain state after awaiting:

:::code language="csharp" source="../snippets/compiled/Grains/GeneralSnippets.cs" id="compress_on_thread_pool":::
Don't read or mutate grain fields inside the <xref:System.Threading.Tasks.Task.Run*> delegate. That code isn't protected by the grain scheduler.

## ConfigureAwait

Don't use `ConfigureAwait(false)` directly in grain methods. It can resume the continuation outside the activation scheduler. General-purpose libraries can use `ConfigureAwait(false)` internally; grain code returns to its scheduler when it normally awaits the library's task. For more information about context capture in .NET, see the [ConfigureAwait FAQ](https://devblogs.microsoft.com/dotnet/configureawait-faq/).

## Start activation-scheduled work

<xref:System.Threading.Tasks.TaskFactory.StartNew*> without an explicit scheduler uses <xref:System.Threading.Tasks.TaskScheduler.Current?displayProperty=nameWithType>, which is the Orleans scheduler in grain code. This is rarely needed; ordinary async methods are clearer. For the .NET scheduling differences, see [Task.Run vs Task.Factory.StartNew](https://devblogs.microsoft.com/dotnet/task-run-vs-task-factory-startnew/).

If an advanced integration passes an async delegate to <xref:System.Threading.Tasks.TaskFactory.StartNew*>, unwrap the nested task:

:::code language="csharp" source="../snippets/compiled/Grains/GeneralSnippets.cs" id="start_async_worker":::
Keep background work under an observed runtime mechanism. Use [grain timers](timers.md), [reminders](reminders.md), a durable job abstraction, or a hosted service according to the required lifetime and reliability.

## Reentrancy still applies

Awaiting external work doesn't let another request run on a non-reentrant grain. Marking a grain reentrant or enabling interleaving can improve throughput while calls wait for I/O, but state can change between turns. See [Request scheduling](request-scheduling.md).
