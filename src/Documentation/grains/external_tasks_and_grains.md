---
layout: page
title: External Tasks and Grains
---

# External Tasks and Grains

By design, any sub-Tasks spawned from grain code (for example, by using `await` or `ContinueWith` or `Task.Factory.StartNew`) will be dispatched on the same per-activation [TPL Task Scheduler](https://msdn.microsoft.com/en-us/library/dd997402(v=vs.110).aspx) as the parent task and therefore inherit the same single-threaded execution model as the rest of grain code. This is the main point behind single threaded execution of [grain turn based concurency](http://dotnet.github.io/orleans/Tutorials/Concurrency).

In some cases grain code might need to “break out” of the Orleans task scheduling model and “do something special”, such as explicitly pointing a `Task` to a different task scheduler or using the .NET Thread pool. An example of such cases is when grain code has to execute a synchronous remote blocking call (such as remote IO). Doing that in the grain context will block the grain as well as one of the Orleans threads and thus should never be made. Instead, the grain code can execute this piece of blocking code on the thread pool thread and join (`await`) the completion of that execution and proceed in the grain context. We expect that escaping from the Orleans scheduler will be a very advanced and seldom required usage scenario beyond the “normal” usage patterns.

### Task based APIs:

1) `await`, `Task.Factory.StartNew` (see below), `Task.ContinuewWith`, `Task.WhenAny`, `Task.WhenAll`, `Task.Delay` all respect the current Task Scheduler. That means that using them in the default way, without passing a different TaskScheduler, will cause them to execute in the grain context.

2) Both `Task.Run` and the `endMethod` delegate of `Task.Factory.FromAsync` do NOT respect the current task Scheduler. They both use the `TaskScheduler.Default` scheduler, which is the .NET thread pool task Scheduler. Therefore, the code inside `Task.Run` and the `endMethod` in `Task.Factory.FromAsync` will ALWAYS run on the .NET thread pool outside of the single-threaded execution model for Orleans grains, [as detailed here](http://blogs.msdn.com/b/pfxteam/archive/2011/10/24/10229468.aspx). However, any code after the `await Task.Run` or `await Task.Factory.FromAsync` will run back under the scheduler at the point the task was created, which is the grain scheduler.

3) `ConfigureAwait(false)` is an explicit API to escape the current task Scheduler. It will cause the code after an awaited Task to be executed on the `TaskScheduler.Default` scheduler, which is the .NET thread pool, and will thus break the single-threaded execution of the Orleans grain. You should in general **never ever use `ConfigureAwait(false)` directly in grain code.**

4) Methods with signature `async void` should not be used with grains. They are intended for graphical user interface event handlers.

#### Task.Factory.StartNew and async delegates
The usual recommendation for scheduling tasks in any C# program is to use `Task.Run` in favor of `Task.Factory.StartNew`.
In fact, a quick google search on the use of `Task.Factory.StartNew()` will suggest [that it is Dangerous](https://blog.stephencleary.com/2013/08/startnew-is-dangerous.html) and [that one should always favor `Task.Run`](https://devblogs.microsoft.com/pfxteam/task-run-vs-task-factory-startnew/). But if we want to stay in the Orleans single threaded execution model for our grain then we need to use it, so how do we do it correctly then?
The "danger" when using `Task.Factory.StartNew()` is that it does not natively support async delegates.
This means that this is likely a bug: `var notIntendedTask = Task.Factory.StartNew(SomeDelegateAsync)`.
`notIntendedTask` is _not_ a task that completes when `SomeDelegateAsync` does.
Instead, one should _always_ unwrap the returned task: `var task = Task.Factory.StartNew(SomeDelegateAsync).Unwrap()`.

### Example:

Below is sample code that demonstrates the usage of `TaskScheduler.Current`, `Task.Run` and a special custom scheduler to escape from Orlean grain context and how to get back to it.

``` csharp
   public async Task MyGrainMethod()
   {
        // Grab the Orleans task scheduler
        var orleansTs = TaskScheduler.Current;
        await TaskDelay(10000);
        // Current task scheduler did not change, the code after await is still running in the same task scheduler.
        Assert.AreEqual(orleansTs, TaskScheduler.Current);

        Task t1 = Task.Run( () =>
        {
             // This code runs on the thread pool scheduler, not on Orleans task scheduler
             Assert.AreNotEqual(orleansTS, TaskScheduler.Current);
             Assert.AreEqual(TaskScheduler.Default, TaskScheduler.Current);
        } );
        await t1;
        // We are back to the Orleans task scheduler. 
        // Since await was executed in Orleans task scheduler context, we are now back to that context.
        Assert.AreEqual(orleansTS, TaskScheduler.Current);

        // Example of using ask.Factory.StartNew with a custom scheduler to escape from the Orleans scheduler
        Task t2 = Task.Factory.StartNew(() =>
        {
             // This code runs on the MyCustomSchedulerThatIWroteMyself scheduler, not on the Orleans task scheduler
             Assert.AreNotEqual(orleansTS, TaskScheduler.Current);
             Assert.AreEqual(MyCustomSchedulerThatIWroteMyself, TaskScheduler.Current);
        },
        CancellationToken.None, TaskCreationOptions.None,
        scheduler: MyCustomSchedulerThatIWroteMyself);
        await t2;
        // We are back to Orleans task scheduler.
        Assert.AreEqual(orleansTS, TaskScheduler.Current);
   }
```

### Advanced Example - making a grain call from code that runs on a thread pool

An even more advanced scenario is a piece of grain code that needs to “break out” of the Orleans task scheduling model and run on a thread pool (or some other, non-Orleans context), but still needs to call another grain. If you try to make a grain call but are not within an Orleans context, you will get an exception that says you are "trying to send a message on a silo not from within a grain and not from within a system target (RuntimeContext is not set to SchedulingContext)".

Below is code that demonstrates how a grain call can be made from a piece of code that runs inside a grain but not in the grain context.

``` csharp
   public async Task MyGrainMethod()
   {
        // Grab the Orleans task scheduler
        var orleansTs = TaskScheduler.Current;
        Task<int> t1 = Task.Run(async () =>
        {
             // This code runs on the thread pool scheduler, not on Orleans task scheduler
             Assert.AreNotEqual(orleansTS, TaskScheduler.Current);
             // You can do whatever you need to do here. Now let's say you need to make a grain call.
             Task<Task<int>> t2 = Task.Factory.StartNew(() =>
             {
                // This code runs on the Orleans task scheduler since we specified the scheduler: orleansTs.
                Assert.AreEqual(orleansTS, TaskScheduler.Current);
                return GrainFactory.GetGrain<IFooGrain>(0).MakeGrainCall();
             }, CancellationToken.None, TaskCreationOptions.None, scheduler: orleansTs);

             int res = await (await t2); // double await, unrelated to Orleans, just part of TPL APIs.
             // This code runs back on the thread pool scheduler, not on the Orleans task scheduler
             Assert.AreNotEqual(orleansTS, TaskScheduler.Current);
             return res;
        } );

        int result = await t1;
        // We are back to the Orleans task scheduler.
        // Since await was executed in the Orleans task scheduler context, we are now back to that context.
        Assert.AreEqual(orleansTS, TaskScheduler.Current);
   }
```
### Dealing with libraries

Some external libraries that your code is using might be using `ConfigureAwait(false)` internally. In fact, it is a good and correct practice in .NET to use `ConfigureAwait(false)` [when implementing general purpose libraries](https://msdn.microsoft.com/en-us/magazine/jj991977.aspx). This is not a problem in Orleans. As long as the code in the grain that invokes the library method is awaiting the library call with a regular `await`, the grain code is correct. The result will be exactly as desired -- the library code will run continuations on the Default scheduler (which happens to be `ThreadPoolTaskScheduler` but it does not guarantee that the continuations will definitely run on a ThreadPool thread, as continuations are often inlined in the previous thread), while the grain code will run on the Orleans scheduler.

Another frequently-asked question is whether there is a need to execute library calls with `Task.Run` -- that is, whether there is a need to explicitly offload the library code to ThreadPool (for grain code to do `Task.Run(()=> myLibrary.FooAsync())`). The answer is No. There is no need to offload any code to ThreadPool, except for the case of library code that is making a blocking synchronous calls. Usually, any well-written and correct .NET async library (methods that return `Task` and are named with an `Async` suffix) do not make blocking calls. Thus there is no need to offload anything to ThreadPool, unless you suspect the async library is buggy or if you are deliberately using a synchronous blocking library.

## Summary

What are you trying to do?   | How to do it
------------- | -------------
Run background work on .NET thread-pool threads. No grain code or grain calls allowed.  |  `Task.Run`
Grain interface call | Method return types = `Task` or `Task<T>`
Run asynchronous worker task from grain code with Orleans turn-based concurrency guarantees ([see above](#taskfactorystartnew-and-async-delegates)). | `Task.Factory.StartNew(WorkerAsync).Unwrap()`
Run synchronous worker task from grain code with Orleans turn-based concurrency guarantees. | `Task.Factory.StartNew(WorkerSync)`
Timeouts for executing work items  | `Task.Delay` + `Task.WhenAny`
Use with `async`/`await` | The normal .NET Task-Async programming model. Supported & recommended  
`ConfigureAwait(false)` | Do not use inside grain code. Allowed only inside libraries.
Calling async library  |  `await` the library call
