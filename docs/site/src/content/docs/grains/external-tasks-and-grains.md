---
title: External tasks and grains
description: Learn about external tasks and grains in .NET Orleans.
ms.date: 03/31/2025
ms.topic: concept-article
---

# External tasks and grains

By design, any sub-tasks spawned from grain code (e.g., using `await`, `ContinueWith`, or `Task.Factory.StartNew`) dispatch on the same per-activation <xref:System.Threading.Tasks.TaskScheduler> as the parent task. Therefore, they inherit the same *single-threaded execution model* as the rest of the grain code. This is the main point behind the single-threaded execution of grain turn-based concurrency.

In some cases, grain code might need to "break out" of the Orleans task scheduling model and "do something special," such as explicitly pointing a <xref:System.Threading.Tasks.Task> to a different task scheduler or the .NET <xref:System.Threading.ThreadPool>. An example is when grain code needs to execute a synchronous remote blocking call (like remote I/O). Executing that blocking call in the grain context blocks the grain and thus should never be done. Instead, the grain code can execute this piece of blocking code on a thread pool thread, join (`await`) the completion of that execution, and then proceed in the grain context. Escaping the Orleans scheduler is expected to be a very advanced and seldom-required usage scenario beyond typical usage patterns.

## Task-based APIs

1. `await`, <xref:System.Threading.Tasks.TaskFactory.StartNew*?displayProperty=nameWithType> (see below), <xref:System.Threading.Tasks.Task.ContinueWith*?displayProperty=nameWithType>, <xref:System.Threading.Tasks.Task.WhenAny*?displayProperty=nameWithType>, <xref:System.Threading.Tasks.Task.WhenAll*?displayProperty=nameWithType>, and <xref:System.Threading.Tasks.Task.Delay*?displayProperty=nameWithType> all respect the current task scheduler. This means using them in the default way, without passing a different <xref:System.Threading.Tasks.TaskScheduler>, causes them to execute in the grain context.

1. Both <xref:System.Threading.Tasks.Task.Run*?displayProperty=nameWithType> and the `endMethod` delegate of <xref:System.Threading.Tasks.TaskFactory.FromAsync*?displayProperty=nameWithType> do *not* respect the current task scheduler. They both use the `TaskScheduler.Default` scheduler, the .NET thread pool task scheduler. Therefore, code inside `Task.Run` and the `endMethod` in `Task.Factory.FromAsync` *always* runs on the .NET thread pool, outside the single-threaded execution model for Orleans grains. However, any code after `await Task.Run` or `await Task.Factory.FromAsync` runs back under the scheduler active at the point the task was created, which is the grain's scheduler.

1. <xref:System.Threading.Tasks.Task.ConfigureAwait*?displayProperty=nameWithType> with `false` is an explicit API to escape the current task scheduler. It causes the code after an awaited <xref:System.Threading.Tasks.Task> to execute on the <xref:System.Threading.Tasks.TaskScheduler.Default*?displayProperty=nameWithType> scheduler (the .NET thread pool), thus breaking the single-threaded execution of the grain.

    > [!CAUTION]
    > Generally, **never use `ConfigureAwait(false)` directly in grain code.**

1. Methods with the signature `async void` should not be used with grains. They are intended for graphical user interface event handlers. An `async void` method can immediately crash the current process if it allows an exception to escape, with no way to handle the exception. This also applies to `List<T>.ForEach(async element => ...)` and any other method accepting an <xref:System.Action`1>, since the asynchronous delegate coerces into an `async void` delegate.

### `Task.Factory.StartNew` and `async` delegates

The usual recommendation for scheduling tasks in C# is using `Task.Run` instead of `Task.Factory.StartNew`. A quick web search for `Task.Factory.StartNew` suggests [it's dangerous](https://blog.stephencleary.com/2013/08/startnew-is-dangerous.html) and [favoring `Task.Run` is always recommended](https://devblogs.microsoft.com/pfxteam/task-run-vs-task-factory-startnew/). However, to stay within the grain's *single-threaded execution model*, `Task.Factory.StartNew` must be used. So, how to use it correctly? The danger with `Task.Factory.StartNew()` is its lack of native support for async delegates. This means code like `var notIntendedTask = Task.Factory.StartNew(SomeDelegateAsync)` is likely a bug. `notIntendedTask` is *not* a task completing when `SomeDelegateAsync` finishes. Instead, *always* unwrap the returned task: `var task = Task.Factory.StartNew(SomeDelegateAsync).Unwrap()`.

#### Example: Multiple tasks and the task scheduler

Below is sample code demonstrating using `TaskScheduler.Current`, `Task.Run`, and a special custom scheduler to escape the Orleans grain context and how to return to it.

```csharp
public async Task MyGrainMethod()
{
    // Grab the grain's task scheduler
    var orleansTS = TaskScheduler.Current;
    await Task.Delay(10_000);

    // Current task scheduler did not change, the code after await is still running
    // in the same task scheduler.
    Assert.AreEqual(orleansTS, TaskScheduler.Current);

    Task t1 = Task.Run(() =>
    {
        // This code runs on the thread pool scheduler, not on Orleans task scheduler
        Assert.AreNotEqual(orleansTS, TaskScheduler.Current);
        Assert.AreEqual(TaskScheduler.Default, TaskScheduler.Current);
    });

    await t1;

    // We are back to the Orleans task scheduler.
    // Since await was executed in Orleans task scheduler context, we are now back
    // to that context.
    Assert.AreEqual(orleansTS, TaskScheduler.Current);

    // Example of using Task.Factory.StartNew with a custom scheduler to escape from
    // the Orleans scheduler
    Task t2 = Task.Factory.StartNew(() =>
    {
        // This code runs on the MyCustomSchedulerThatIWroteMyself scheduler, not on
        // the Orleans task scheduler
        Assert.AreNotEqual(orleansTS, TaskScheduler.Current);
        Assert.AreEqual(MyCustomSchedulerThatIWroteMyself, TaskScheduler.Current);
    },
    CancellationToken.None,
    TaskCreationOptions.None,
    scheduler: MyCustomSchedulerThatIWroteMyself);

    await t2;

    // We are back to Orleans task scheduler.
    Assert.AreEqual(orleansTS, TaskScheduler.Current);
}
```

#### Example: Make a grain call from code running on a thread pool thread

Another scenario involves grain code needing to "break out" of the grain's task scheduling model and run on a thread pool thread (or some other non-grain context) but still needing to call another grain. Grain calls can be made from non-grain contexts without extra ceremony.

The following code demonstrates making a grain call from code running inside a grain but not in the grain context.

```csharp
public async Task MyGrainMethod()
{
    // Grab the Orleans task scheduler
    var orleansTS = TaskScheduler.Current;
    var fooGrain = this.GrainFactory.GetGrain<IFooGrain>(0);
    Task<int> t1 = Task.Run(async () =>
    {
        // This code runs on the thread pool scheduler,
        // not on Orleans task scheduler
        Assert.AreNotEqual(orleansTS, TaskScheduler.Current);
        int res = await fooGrain.MakeGrainCall();

        // This code continues on the thread pool scheduler,
        // not on the Orleans task scheduler
        Assert.AreNotEqual(orleansTS, TaskScheduler.Current);
        return res;
    });

    int result = await t1;

    // We are back to the Orleans task scheduler.
    // Since await was executed in the Orleans task scheduler context,
    // we are now back to that context.
    Assert.AreEqual(orleansTS, TaskScheduler.Current);
}
```

## Work with libraries

Some external libraries used by the code might use `ConfigureAwait(false)` internally. Using `ConfigureAwait(false)` is a good and correct practice in .NET [when implementing general-purpose libraries](https://devblogs.microsoft.com/dotnet/configureawait-faq/#when-should-i-use-configureawaitfalse). This isn't a problem in Orleans. As long as the grain code invoking the library method awaits the library call with a regular `await`, the grain code is correct. The result is exactly as desired: the library code runs continuations on the default scheduler (the value returned by `TaskScheduler.Default`, which doesn't guarantee continuations run on a <xref:System.Threading.ThreadPool> thread, as they are often inlined on the previous thread), while the grain code runs on the grain's scheduler.

Another frequently asked question is whether library calls need executing with `Task.Run`—that is, whether library code needs explicit offloading to the <xref:System.Threading.ThreadPool> (e.g., `await Task.Run(() => myLibrary.FooAsync())`). The answer is no. Offloading code to the <xref:System.Threading.ThreadPool> isn't necessary except when library code makes blocking synchronous calls. Usually, any well-written and correct .NET async library (methods returning <xref:System.Threading.Tasks.Task> and named with an `Async` suffix) doesn't make blocking calls. Thus, offloading anything to the <xref:System.Threading.ThreadPool> isn't needed unless the async library is suspected to be buggy or a synchronous blocking library is deliberately used.

## Deadlocks

Since grains execute single-threaded, deadlocking a grain is possible by synchronously blocking in a way requiring multiple threads to unblock. This means code calling any of the following methods and properties can deadlock a grain if the provided tasks haven't completed by the time the method or property is invoked:

- `Task.Wait()`
- `Task.Result`
- `Task.WaitAny(...)`
- `Task.WaitAll(...)`
- `task.GetAwaiter().GetResult()`

Avoid these methods in any high-concurrency service because they can lead to poor performance and instability. They starve the .NET <xref:System.Threading.ThreadPool> by blocking threads that could perform useful work and require the <xref:System.Threading.ThreadPool> to inject additional threads for completion. When executing grain code, these methods can cause the grain to deadlock, so avoid them in grain code as well.

If some *sync-over-async* work is unavoidable, moving that work to a separate scheduler is best. The simplest way is using `await Task.Run(() => task.Wait())`, for example. Note that avoiding *sync-over-async* work is strongly recommended, as it harms application scalability and performance.

## Summary: Working with tasks in Orleans

| What are you trying to do? | How to do it |
|--|--|
| Run background work on .NET thread-pool threads. No grain code or grain calls are allowed. | `Task.Run` |
| Run asynchronous worker task from grain code with Orleans turn-based concurrency guarantees ([see above](#taskfactorystartnew-and-async-delegates)). | `Task.Factory.StartNew(WorkerAsync).Unwrap()` (<xref:System.Threading.Tasks.TaskExtensions.Unwrap*>) |
| Run synchronous worker task from grain code with Orleans turn-based concurrency guarantees. | `Task.Factory.StartNew(WorkerSync)` |
| Timeouts for executing work items | `Task.Delay` + `Task.WhenAny` |
| Call an asynchronous library method | `await` the library call |
| Use `async`/`await` | The normal .NET Task-Async programming model. Supported & recommended |
| `ConfigureAwait(false)` | Do not use inside grain code. Allowed only inside libraries. |
