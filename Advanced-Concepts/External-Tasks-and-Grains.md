---
layout: page
title: External Tasks and Grains
---
{% include JB/setup %}

[[THIS SECTON NEEDS TO BE UPDATED]]

By design, any sub-Tasks spawned from grain code (for example, by using `await` or `ContinueWith` or `Task.Factory.StartNew`) will be dispatched on the same per-activation [TPL Task Scheduler](https://msdn.microsoft.com/en-us/library/dd997402(v=vs.110).aspx) as the parent task and therefore inherit the same single-threaded execution model as the rest of grain code. This is the main point behind single threaded execution of [grain turn based concurency](http://dotnet.github.io/orleans/Step-by-step-Tutorials/Concurrency).

In some cases grain code might need to “break out” of the Orleans task scheduling model and “do something special”, such as explicitly pointing a Task to a different task scheduler or using the .NET Thread pool. Example of such cases may be when the grain code has to execute a synchronous remote blocking call (such as remote IO). Doing that in the grain context will block the grain as well as one of the Orleans threads and thus should never be made. Instead, the grain code can execute this piece of blocking code on the thread pool thread and join (`await`) the completion of that execution and proceed in the grain context. However, our working hypothesis is this will be a very advanced and seldom required usage scenario beyond the “normal” usage patterns.

## Task based APIs:

1) `await`, `Task.Factory.StartNew`, `Task.ContinuewWith`, `Task.WhenAny`, `Task.WhenAll`, `Task.Delay` all respect the current Task Scheduler. That means that using them in the default way, without passing a different TaskScheduler, will cause them to execte in the grain context.

2) Both `Task.Run` and the `endMethod` delegate of `Task.Factory.FromAsync` do NOT respect the current task Scheduler. They both use the TaskScheduler.Default scheduler, which is the .NET thread pool task Scheduler. Therefore, they will ALWAYS run on .NET thread pool outside of the single-threaded execution model for Orleans grains, [as detailed here](http://blogs.msdn.com/b/pfxteam/archive/2011/10/24/10229468.aspx). Any continuation of `await` code chained to them will run back under the “current” scheduler at the point the composite task was created, which is the grain context. 


3) Methods with signature 'async void' should not be used with grains, they are intended for graphical user interface event handlers.


For reference, this is a sample of one of the many canonical unit test case(s) we have for scheduler / sub-task behavior in the Orleans BVT / Nightly test suites:

``` csharp
   public Task MyGrainMethod()
    {
        // This works.
        var orleansTS = TaskScheduler.Current; // Grabs the Orleans task scheduler
        await TaskDelay(10000);
        Assert.AreEqual(orleansTS, TaskScheduler.Current); // Current task scheduler did not change, the code after await is        still running in the same task scheduler
        
        Task.Run
        Task t1 = Task.Run( () => 
             { 
                 // This code runs on the thread pool scheduler, not on Orleans task scheduler
                Assert.AreNotEqual(orleansTS, TaskScheduler.Current);
                Assert.AreEqual(TaskScheduler.Default, TaskScheduler.Current); 
             }  );
        await t1;
        Assert.AreEqual(orleansTS, TaskScheduler.Current); // We are back to Orleans task scheduler, since await was executed in  Orleans task scheduler context we are now back to that context.
        
        Task t2 = Task.Factory.StartNew(() =>
                {
                   // This code runs on MyCustomSchedulerThatIWroteMyself scheduler, not on Orleans task scheduler
                Assert.AreNotEqual(orleansTS, TaskScheduler.Current);
                Assert.AreEqual(MyCustomSchedulerThatIWroteMyself, TaskScheduler.Current); 
                },
                CancellationToken.None, TaskCreationOptions.None,
                scheduler: MyCustomSchedulerThatIWroteMyself);
        await t2;
        Assert.AreEqual(orleansTS, TaskScheduler.Current); // We are back to Orleans task scheduler.
    }
```
    
``` csharp
[TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Scheduler")]
public async Task Sched_Task_WhenAny_Busy_Timeout()
{
    TaskScheduler scheduler = new ActivationTaskScheduler(masterScheduler.Pool);
    ResultHandle pause1 = new ResultHandle();
    ResultHandle pause2 = new ResultHandle();
    ResultHandle finish = new ResultHandle();
    Task<int> task1 = null;
    Task<int> task2 = null;
    Task join = null;

    SafeRandom random = new SafeRandom();
    Task wrapper = new Task(() =>
    {
        task1 = Task<int>.Factory.StartNew(() =>
        {
            Console.WriteLine("Task-1 Started");
            Assert.AreEqual(scheduler, TaskScheduler.Current);

            int num1 = 1;
            while (!pause1.Done) // Infinite busy loop
            {
                num1 = random.Next();
            }

            Console.WriteLine("Task-1 Done");
            return num1;
        });

        task2 = Task<int>.Factory.StartNew(() =>
        {
            Console.WriteLine("Task-2 Started");
            Assert.AreEqual(scheduler, TaskScheduler.Current);

            int num2 = 2;
            while (!pause2.Done) // Infinite busy loop
            {
                num2 = random.Next();
            }
            Console.WriteLine("Task-2 Done");
            return num2;
        });

        join = Task.WhenAny(task1, task2, Task.Delay(TimeSpan.FromSeconds(2)));
        finish.Done = true;
    });

    wrapper.Start(scheduler);
    finish.WaitForFinished(TimeSpan.FromSeconds(1));

    await join;
    Assert.IsTrue(join.IsCompleted && !join.IsFaulted, "Join Status " + join.Status);
    Assert.IsFalse(task1.IsFaulted, "Task-1 Faulted " + task1.Exception);
    Assert.IsFalse(task1.IsCompleted, "Task-1 Status " + task1.Status);
    Assert.IsFalse(task2.IsFaulted, "Task-2 Faulted " + task2.Exception);
    Assert.IsFalse(task2.IsCompleted, "Task-2 Status " + task2.Status);
}
```

## Summary

What are you trying to do?   | How to do it 
------------- | -------------
Not block compute threads during external IO calls  |  Task.Factory.FromAsync 
Run background work on .NET thread-pool threads. No grain code or grain calls allowed.  |  Task.Run
Run worker task from grain code with Orleans turn-based concurrency guarantees. | Task.Factory.StartNew  
Timeouts for executing work items  | Task.Delay + Task.WhenAny  
Grain interface | Method return types = Task or Task<T> 
Use with async/await | The normal .NET Task-Async programming model. Supported & recommended  
