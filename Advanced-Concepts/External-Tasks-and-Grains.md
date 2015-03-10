---
layout: page
title: External Tasks and Grains
---
{% include JB/setup %}

[[THIS SECTON NEEDS TO BE UPDATED]]

By design, any sub-Tasks spawned from grain code (for example, by using Task.Factory.StartNew) will inherit the same single-threaded execution model as for grain code.

 It is possible to “break out” of the Orleans task scheduling model by “doing something special”, such as explicitly pointing a Task to a different task scheduler (e.g. TaskScheduler.Default to run on .NET thread pool). However, our working hypothesis is this will be a very advanced and seldom required usage scenario beyond the “normal” usage patterns described below.

 Both Task.Delay and Task.Factory.FromAsync use .NET timer and/or .NET thread pool under the covers (which counts as “doing something special” for the purposes of this discussion), and so the normal Task.Delay / Timeout and FromAsync scenarios should work exactly the way you would intuitively expect them to when run under Orleans task scheduler.

 Similarly, Task.Run will ALWAYS run outside of the single-threaded execution model for Orleans grains, as it runs on .NET thread pool [as detailed here](http://blogs.msdn.com/b/pfxteam/archive/2011/10/24/10229468.aspx).

 Whether new Task(...) and Task.Factory.StartNew work the way you “expect” them to depends on what you are trying to do. 

 By default, those new sub-Tasks WILL run subject to the single-threaded execution model for that grain, but possible to do various unnatural acts to escape.

 Again, our working hypothesis is that this is the “right” behavior for “least surprises”.

Composite tasks primitives such as Task.WhenAny or Task.WhenAll can sometimes be a little tricky, because they themselves are “triggered” by .NET framework automatically based on state of input tasks, but any ContinueWith code chained to the composite (or code after await of that composite) will run under the “current” scheduler at the point the composite task was created. 

 In almost all cases though (modulo some extremely advanced Task scenarios) you should get the result you expect & want – When used from inside grain they will “Join” execution back into running under the Orleans per-activation task scheduler & single threaded execution model for that grain.


As a side-note, methods with signature 'async void' should not be used with grains, they are intended for graphical user interface event handlers.
For reference, this is a sample of one of the many canonical unit test case(s) we have for scheduler / sub-task behavior in the Orleans BVT / Nightly test suites:



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





## Summary


  



What are you trying to do?   | How to do it 
------------- | -------------
Not block compute threads during external IO calls  |  Task.Factory.FromAsync 
Run background work on .NET thread-pool threads. No grain code or grain calls allowed.  |  Task.Run
Run worker task from grain code with Orleans turn-based concurrency guarantees. | Task.Factory.StartNew  
Timeouts for executing work items  | Task.Delay + Task.WhenAny  
Grain interface | Method return types = Task or Task<T> 
Use with async/await | The normal .NET Task-Async programming model. Supported & recommended  