using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.TestHooks;
using Orleans.Statistics;
using TestExtensions;
using UnitTests.Grains;
using UnitTests.TesterInternal;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.SchedulerTests
{
    public class OrleansTaskSchedulerAdvancedTests_Set2 : IDisposable
    {
        private static readonly object Lockable = new object();
        private static readonly int WaitFactor = Debugger.IsAttached ? 100 : 1;
        private readonly ITestOutputHelper output;
        private readonly UnitTestSchedulingContext context;

        private readonly IHostEnvironmentStatistics performanceMetrics;
        private readonly ILoggerFactory loggerFactory;
        public OrleansTaskSchedulerAdvancedTests_Set2(ITestOutputHelper output)
        {
            this.output = output;
            this.loggerFactory = OrleansTaskSchedulerBasicTests.InitSchedulerLogging();
            this.context = new UnitTestSchedulingContext();
            this.performanceMetrics = new TestHooksHostEnvironmentStatistics();
        }
        
        public void Dispose()
        {
            this.loggerFactory.Dispose();
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public void ActivationSched_SimpleFifoTest()
        {
            // This is not a great test because there's a 50/50 shot that it will work even if the scheduling
            // is completely and thoroughly broken and both closures are executed "simultaneously"
            using var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            TaskScheduler scheduler = workItemGroup.TaskScheduler;

            int n = 0;
            // ReSharper disable AccessToModifiedClosure
            Task task1 = new Task(() => { Thread.Sleep(1000); n = n + 5; });
            Task task2 = new Task(() => { n = n * 3; });
            // ReSharper restore AccessToModifiedClosure

            task1.Start(scheduler);
            task2.Start(scheduler);

            // Pause to let things run
            Thread.Sleep(1500);

            // N should be 15, because the two tasks should execute in order
            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(15, n);  // "Work items executed out of order"
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public void ActivationSched_NewTask_ContinueWith_Wrapped()
        {
            using var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            TaskScheduler scheduler = workItemGroup.TaskScheduler;

            Task<Task> wrapped = new Task<Task>(() =>
            {
                this.output.WriteLine("#0 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                    SynchronizationContext.Current, TaskScheduler.Current);

                Task t0 = new Task(() =>
                {
                    this.output.WriteLine("#1 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.Equal(scheduler, TaskScheduler.Current);  // "TaskScheduler.Current #1"
                });
                Task t1 = t0.ContinueWith(task =>
                {
                    Assert.False(task.IsFaulted, "Task #1 Faulted=" + task.Exception);

                    this.output.WriteLine("#2 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.Equal(scheduler, TaskScheduler.Current);  // "TaskScheduler.Current #2"
                });
                t0.Start(scheduler);
                return t1;
            });
            wrapped.Start(scheduler);
            bool ok = wrapped.Unwrap().Wait(TimeSpan.FromSeconds(2));
            Assert.True(ok, "Finished OK");
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public void ActivationSched_SubTaskExecutionSequencing()
        {
            using var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            TaskScheduler scheduler = workItemGroup.TaskScheduler;

            LogContext("Main-task " + Task.CurrentId);

            int n = 0;

            Action action = () =>
            {
                LogContext("WorkItem-task " + Task.CurrentId);

                for (int i = 0; i < 10; i++)
                {
                    int id = -1;
                    Task.Factory.StartNew(() =>
                    {
                        id = Task.CurrentId.HasValue ? (int)Task.CurrentId : -1;

                        // ReSharper disable AccessToModifiedClosure
                        LogContext("Sub-task " + id + " n=" + n);
                        int k = n;
                        this.output.WriteLine("Sub-task " + id + " sleeping");
                        Thread.Sleep(100);
                        this.output.WriteLine("Sub-task " + id + " awake");
                        n = k + 1;
                        // ReSharper restore AccessToModifiedClosure
                    })
                    .ContinueWith(tsk =>
                    {
                        LogContext("Sub-task " + id + "-ContinueWith");

                        this.output.WriteLine("Sub-task " + id + " Done");
                    });
                }
            };

            Task t = new Task(action);

            t.Start(scheduler);

            // Pause to let things run
            this.output.WriteLine("Main-task sleeping");
            Thread.Sleep(TimeSpan.FromSeconds(2));
            this.output.WriteLine("Main-task awake");

            // N should be 10, because all tasks should execute serially
            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(10, n);  // "Work items executed concurrently"
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task ActivationSched_ContinueWith_1_Test()
        {
            using var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            TaskScheduler scheduler = workItemGroup.TaskScheduler;

            var result = new TaskCompletionSource<bool>();
            int n = 0;

            Task wrapper = new Task(() =>
            {
                // ReSharper disable AccessToModifiedClosure
                Task task1 = Task.Factory.StartNew(() => { this.output.WriteLine("===> 1a"); Thread.Sleep(1000); n = n + 3; this.output.WriteLine("===> 1b"); });
                Task task2 = task1.ContinueWith(task => { n = n * 5; this.output.WriteLine("===> 2"); });
                Task task3 = task2.ContinueWith(task => { n = n / 5; this.output.WriteLine("===> 3"); });
                Task task4 = task3.ContinueWith(task => { n = n - 2; this.output.WriteLine("===> 4"); result.SetResult(true); });
                // ReSharper restore AccessToModifiedClosure
                task4.ContinueWith(task =>
                {
                    this.output.WriteLine("Done Faulted={0}", task.IsFaulted);
                    Assert.False(task.IsFaulted, "Faulted with Exception=" + task.Exception);
                });
            });
            wrapper.Start(scheduler);

            var timeoutLimit = TimeSpan.FromSeconds(2);
            try
            {
                await result.Task.WithTimeout(timeoutLimit);
            }
            catch (TimeoutException)
            {
                Assert.True(false, "Result did not arrive before timeout " + timeoutLimit);
            }

            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(1, n);  // "Work items executed out of order"
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task ActivationSched_WhenAny()
        {
            using var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            TaskScheduler scheduler = workItemGroup.TaskScheduler;

            ManualResetEvent pause1 = new ManualResetEvent(false);
            ManualResetEvent pause2 = new ManualResetEvent(false);
            var finish = new TaskCompletionSource<bool>();
            Task<int> task1 = null;
            Task<int> task2 = null;
            Task join = null;
            Task wrapper = new Task(() =>
            {
                task1 = Task<int>.Factory.StartNew(() =>
                {
                    this.output.WriteLine("Task-1 Started");
                    Assert.Equal(scheduler, TaskScheduler.Current);  // "TaskScheduler.Current=" + TaskScheduler.Current
                    pause1.WaitOne();
                    this.output.WriteLine("Task-1 Done");
                    return 1;
                });
                task2 = Task<int>.Factory.StartNew(() =>
                {
                    this.output.WriteLine("Task-2 Started");
                    Assert.Equal(scheduler, TaskScheduler.Current);
                    pause2.WaitOne();
                    this.output.WriteLine("Task-2 Done");
                    return 2;
                });

                join = Task.WhenAny(task1, task2, Task.Delay(TimeSpan.FromSeconds(2)));

                finish.SetResult(true);
            });
            wrapper.Start(scheduler);

            var timeoutLimit = TimeSpan.FromSeconds(1);
            try
            {
                await finish.Task.WithTimeout(timeoutLimit);
            }
            catch (TimeoutException)
            {
                Assert.True(false, "Result did not arrive before timeout " + timeoutLimit);
            }

            pause1.Set();
            await join;
            Assert.True(join.IsCompleted && !join.IsFaulted, "Join Status " + join.Status);
            Assert.False(task1.IsFaulted, "Task-1 Faulted " + task1.Exception);
            Assert.False(task2.IsFaulted, "Task-2 Faulted " + task2.Exception);
            Assert.True(task1.IsCompleted || task2.IsCompleted, "Task-1 Status = " + task1.Status + " Task-2 Status = " + task2.Status);
            pause2.Set();
            task2.Ignore();
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task ActivationSched_WhenAny_Timeout()
        {
            using var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            TaskScheduler scheduler = workItemGroup.TaskScheduler;

            ManualResetEvent pause1 = new ManualResetEvent(false);
            ManualResetEvent pause2 = new ManualResetEvent(false);
            var finish = new TaskCompletionSource<bool>();
            Task<int> task1 = null;
            Task<int> task2 = null;
            Task join = null;
            Task wrapper = new Task(() =>
            {
                task1 = Task<int>.Factory.StartNew(() =>
                {
                    this.output.WriteLine("Task-1 Started");
                    Assert.Equal(scheduler, TaskScheduler.Current);
                    pause1.WaitOne();
                    this.output.WriteLine("Task-1 Done");
                    return 1;
                });
                task2 = Task<int>.Factory.StartNew(() =>
                {
                    this.output.WriteLine("Task-2 Started");
                    Assert.Equal(scheduler, TaskScheduler.Current);
                    pause2.WaitOne();
                    this.output.WriteLine("Task-2 Done");
                    return 2;
                });

                join = Task.WhenAny(task1, task2, Task.Delay(TimeSpan.FromSeconds(2)));

                finish.SetResult(true);
            });
            wrapper.Start(scheduler);

            var timeoutLimit = TimeSpan.FromSeconds(1);
            try
            {
                await finish.Task.WithTimeout(timeoutLimit);
            }
            catch (TimeoutException)
            {
                Assert.True(false, "Result did not arrive before timeout " + timeoutLimit);
            }

            Assert.NotNull(join);
            await join;
            Assert.True(join.IsCompleted && !join.IsFaulted, "Join Status " + join.Status);
            Assert.False(task1.IsFaulted, "Task-1 Faulted " + task1.Exception);
            Assert.False(task1.IsCompleted, "Task-1 Status " + task1.Status);
            Assert.False(task2.IsFaulted, "Task-2 Faulted " + task2.Exception);
            Assert.False(task2.IsCompleted, "Task-2 Status " + task2.Status);
            pause1.Set();
            task1.Ignore();
            pause2.Set();
            task2.Ignore();
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task ActivationSched_WhenAny_Busy_Timeout()
        {
            using var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            TaskScheduler scheduler = workItemGroup.TaskScheduler;

            var pause1 = new TaskCompletionSource<bool>();
            var pause2 = new TaskCompletionSource<bool>();
            var finish = new TaskCompletionSource<bool>();
            Task<int> task1 = null;
            Task<int> task2 = null;
            Task join = null;
            Task wrapper = new Task(() =>
            {
                task1 = Task<int>.Factory.StartNew(() =>
                {
                    this.output.WriteLine("Task-1 Started");
                    Assert.Equal(scheduler, TaskScheduler.Current);
                    int num1 = 1;
                    while (!pause1.Task.Result) // Infinite busy loop
                    {
                        num1 = ThreadSafeRandom.Next();
                    }
                    this.output.WriteLine("Task-1 Done");
                    return num1;
                });
                task2 = Task<int>.Factory.StartNew(() =>
                {
                    this.output.WriteLine("Task-2 Started");
                    Assert.Equal(scheduler, TaskScheduler.Current);
                    int num2 = 2;
                    while (!pause2.Task.Result) // Infinite busy loop
                    {
                        num2 = ThreadSafeRandom.Next();
                    }
                    this.output.WriteLine("Task-2 Done");
                    return num2;
                });

                join = Task.WhenAny(task1, task2, Task.Delay(TimeSpan.FromSeconds(2)));

                finish.SetResult(true);
            });
            wrapper.Start(scheduler);

            var timeoutLimit = TimeSpan.FromSeconds(1);
            try
            {
                await finish.Task.WithTimeout(timeoutLimit);
            }
            catch (TimeoutException)
            {
                Assert.True(false, "Result did not arrive before timeout " + timeoutLimit);
            }

            Assert.NotNull(join); // Joined promise assigned
            await join;
            Assert.True(join.IsCompleted && !join.IsFaulted, "Join Status " + join.Status);
            Assert.False(task1.IsFaulted, "Task-1 Faulted " + task1.Exception);
            Assert.False(task1.IsCompleted, "Task-1 Status " + task1.Status);
            Assert.False(task2.IsFaulted, "Task-2 Faulted " + task2.Exception);
            Assert.False(task2.IsCompleted, "Task-2 Status " + task2.Status);
        }


        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task ActivationSched_Task_Run()
        {
            using var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            TaskScheduler scheduler = workItemGroup.TaskScheduler;

            ManualResetEvent pause1 = new ManualResetEvent(false);
            ManualResetEvent pause2 = new ManualResetEvent(false);
            var finish = new TaskCompletionSource<bool>();
            Task<int> task1 = null;
            Task<int> task2 = null;
            Task join = null;
            Task wrapper = new Task(() =>
            {
                task1 = Task.Run(() =>
                {
                    this.output.WriteLine("Task-1 Started");
                    Assert.NotEqual(scheduler, TaskScheduler.Current);
                    pause1.WaitOne();
                    this.output.WriteLine("Task-1 Done");
                    return 1;
                });
                task2 = Task.Run(() =>
                {
                    this.output.WriteLine("Task-2 Started");
                    Assert.NotEqual(scheduler, TaskScheduler.Current);
                    pause2.WaitOne();
                    this.output.WriteLine("Task-2 Done");
                    return 2;
                });

                join = Task.WhenAll(task1, task2).ContinueWith(t =>
                {
                    this.output.WriteLine("Join Started");
                    if (t.IsFaulted) throw t.Exception;
                    Assert.Equal(scheduler, TaskScheduler.Current);
                    this.output.WriteLine("Join Done");
                });

                finish.SetResult(true);
            });
            wrapper.Start(scheduler);

            var timeoutLimit = TimeSpan.FromSeconds(1);
            try
            {
                await finish.Task.WithTimeout(timeoutLimit);
            }
            catch (TimeoutException)
            {
                Assert.True(false, "Result did not arrive before timeout " + timeoutLimit);
            }

            pause1.Set();
            pause2.Set();
            Assert.NotNull(join); // Joined promise assigned
            await join;
            Assert.True(join.IsCompleted && !join.IsFaulted, "Join Status " + join);
            Assert.True(task1.IsCompleted && !task1.IsFaulted, "Task-1 Status " + task1);
            Assert.True(task2.IsCompleted && !task2.IsFaulted, "Task-2 Status " + task2);
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task ActivationSched_Task_Run_Delay()
        {
            using var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            TaskScheduler scheduler = workItemGroup.TaskScheduler;

            ManualResetEvent pause1 = new ManualResetEvent(false);
            ManualResetEvent pause2 = new ManualResetEvent(false);
            var finish = new TaskCompletionSource<bool>();
            Task<int> task1 = null;
            Task<int> task2 = null;
            Task join = null;
            Task wrapper = new Task(() =>
            {
                task1 = Task.Run(() =>
                {
                    this.output.WriteLine("Task-1 Started");
                    Assert.NotEqual(scheduler, TaskScheduler.Current);
                    Task.Delay(1);
                    Assert.NotEqual(scheduler, TaskScheduler.Current);
                    pause1.WaitOne();
                    this.output.WriteLine("Task-1 Done");
                    return 1;
                });
                task2 = Task.Run(() =>
                {
                    this.output.WriteLine("Task-2 Started");
                    Assert.NotEqual(scheduler, TaskScheduler.Current);
                    Task.Delay(1);
                    Assert.NotEqual(scheduler, TaskScheduler.Current);
                    pause2.WaitOne();
                    this.output.WriteLine("Task-2 Done");
                    return 2;
                });

                join = Task.WhenAll(task1, task2).ContinueWith(t =>
                {
                    this.output.WriteLine("Join Started");
                    if (t.IsFaulted) throw t.Exception;
                    Assert.Equal(scheduler, TaskScheduler.Current);
                    this.output.WriteLine("Join Done");
                });

                finish.SetResult(true);
            });
            wrapper.Start(scheduler);

            var timeoutLimit = TimeSpan.FromSeconds(1);
            try
            {
                await finish.Task.WithTimeout(timeoutLimit);
            }
            catch (TimeoutException)
            {
                Assert.True(false, "Result did not arrive before timeout " + timeoutLimit);
            }

            pause1.Set();
            pause2.Set();
            Assert.NotNull(join); // Joined promise assigned
            await join;
            Assert.True(join.IsCompleted && !join.IsFaulted, "Join Status " + join);
            Assert.True(task1.IsCompleted && !task1.IsFaulted, "Task-1 Status " + task1);
            Assert.True(task2.IsCompleted && !task2.IsFaulted, "Task-2 Status " + task2);
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task ActivationSched_Task_Delay()
        {
            using var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            TaskScheduler scheduler = workItemGroup.TaskScheduler;

            Task<Task> wrapper = new Task<Task>(async () =>
            {
                Assert.Equal(scheduler, TaskScheduler.Current);
                await DoDelay(1);
                Assert.Equal(scheduler, TaskScheduler.Current);
                await DoDelay(2);
                Assert.Equal(scheduler, TaskScheduler.Current);
            });
            wrapper.Start(scheduler);

            await wrapper.Unwrap();
        }

        private async Task DoDelay(int i)
        {
            try
            {
                this.output.WriteLine("Before Task.Delay #{0} TaskScheduler.Current={1}", i, TaskScheduler.Current);
                await Task.Delay(1);
                this.output.WriteLine("After Task.Delay #{0} TaskScheduler.Current={1}", i, TaskScheduler.Current);
            }
            catch (ObjectDisposedException)
            {
                // Ignore any problems with ObjectDisposedException if console output stream has already been closed
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task ActivationSched_Turn_Execution_Order_Loop()
        {
            using var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            TaskScheduler scheduler = workItemGroup.TaskScheduler;

            const int NumChains = 100;
            const int ChainLength = 3;
            // Can we add a unit test that basicaly checks that any turn is indeed run till completion before any other turn? 
            // For example, you have a long running main turn and in the middle it spawns a lot of short CWs (on Done promise) and StartNew. 
            // You test that no CW/StartNew runs until the main turn is fully done. And run in stress.

            var resultHandles = new TaskCompletionSource<bool>[NumChains];
            Task[] taskChains = new Task[NumChains];
            Task[] taskChainEnds = new Task[NumChains];
            bool[] executingChain = new bool[NumChains];
            int[] stageComplete = new int[NumChains];
            int executingGlobal = -1;
            for (int i = 0; i < NumChains; i++)
            {
                int chainNum = i; // Capture
                int sleepTime = ThreadSafeRandom.Next(100);
                resultHandles[i] = new TaskCompletionSource<bool>();
                taskChains[i] = new Task(() =>
                {
                    const int taskNum = 0;
                    try
                    {
                        Assert.Equal(-1, executingGlobal);  // "Detected unexpected other execution in chain " + chainNum + " Task " + taskNum
                        Assert.False(executingChain[chainNum], "Detected unexpected other execution on chain " + chainNum + " Task " + taskNum);

                        executingGlobal = chainNum;
                        executingChain[chainNum] = true;

                        Thread.Sleep(sleepTime);
                    }
                    finally
                    {
                        stageComplete[chainNum] = taskNum;
                        executingChain[chainNum] = false;
                        executingGlobal = -1;
                    }
                });
                Task task = taskChains[i];
                for (int j = 1; j < ChainLength; j++)
                {
                    int taskNum = j; // Capture
                    task = task.ContinueWith(t =>
                    {
                        if (t.IsFaulted) throw t.Exception;
                        this.output.WriteLine("Inside Chain {0} Task {1}", chainNum, taskNum);
                        try
                        {
                            Assert.Equal(-1, executingGlobal);  // "Detected unexpected other execution in chain " + chainNum + " Task " + taskNum
                            Assert.False(executingChain[chainNum], "Detected unexpected other execution on chain " + chainNum + " Task " + taskNum);
                            Assert.Equal(taskNum - 1, stageComplete[chainNum]);  // "Detected unexpected execution stage on chain " + chainNum + " Task " + taskNum

                            executingGlobal = chainNum;
                            executingChain[chainNum] = true;

                            Thread.Sleep(sleepTime);
                        }
                        finally
                        {
                            stageComplete[chainNum] = taskNum;
                            executingChain[chainNum] = false;
                            executingGlobal = -1;
                        }
                    }, scheduler);
                }
                taskChainEnds[chainNum] = task.ContinueWith(t =>
                {
                    if (t.IsFaulted) throw t.Exception;
                    this.output.WriteLine("Inside Chain {0} Final Task", chainNum);
                    resultHandles[chainNum].SetResult(true);
                }, scheduler);
            }

            for (int i = 0; i < NumChains; i++)
            {
                taskChains[i].Start(scheduler);
            }

            for (int i = 0; i < NumChains; i++)
            {
                TimeSpan waitCheckTime = TimeSpan.FromMilliseconds(150 * ChainLength * NumChains * WaitFactor);

                try
                {
                    await resultHandles[i].Task.WithTimeout(waitCheckTime);
                }
                catch (TimeoutException)
                {
                    Assert.True(false, "Result did not arrive before timeout " + waitCheckTime);
                }

                bool ok = resultHandles[i].Task.Result;
                
                try
                {
                    // since resultHandle being complete doesn't directly imply that the final chain was completed (there's a chance for a race condition), give a small chance for it to complete.
                    await taskChainEnds[i].WithTimeout(TimeSpan.FromMilliseconds(10));
                }
                catch (TimeoutException)
                {
                    Assert.True(false, $"Task chain end {i} should complete very shortly after after its resultHandle");
                }

                Assert.True(taskChainEnds[i].IsCompleted, "Task chain " + i + " should be completed");
                Assert.False(taskChainEnds[i].IsFaulted, "Task chain " + i + " should not be Faulted: " + taskChainEnds[i].Exception);
                Assert.Equal(ChainLength - 1, stageComplete[i]);  // "Task chain " + i + " should have completed all stages"
                Assert.True(ok, "Successfully waited for ResultHandle for Task chain " + i);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task ActivationSched_Test1()
        {
            using var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            TaskScheduler scheduler = workItemGroup.TaskScheduler;

            await Run_ActivationSched_Test1(scheduler, false);
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task ActivationSched_Test1_Bounce()
        {
            using var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            TaskScheduler scheduler = workItemGroup.TaskScheduler;

            await Run_ActivationSched_Test1(scheduler, true);
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task OrleansSched_Test1()
        {
            UnitTestSchedulingContext context = new UnitTestSchedulingContext();
            using var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, this.loggerFactory);
            ActivationTaskScheduler scheduler = workItemGroup.TaskScheduler;

            await Run_ActivationSched_Test1(scheduler, false);
        }
        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task OrleansSched_Test1_Bounce()
        {
            UnitTestSchedulingContext context = new UnitTestSchedulingContext();
            using var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, this.loggerFactory);
            ActivationTaskScheduler scheduler = workItemGroup.TaskScheduler;

            await Run_ActivationSched_Test1(scheduler, true);
        }

        internal async Task Run_ActivationSched_Test1(TaskScheduler scheduler, bool bounceToThreadPool)
        {
            var grainId = LegacyGrainId.GetGrainId(0, Guid.NewGuid());
            var silo = new MockSiloDetails
            {
                SiloAddress = SiloAddressUtils.NewLocalSiloAddress(23)
            };
            var grain = new NonReentrentStressGrainWithoutState();

            await Task.Factory.StartNew(() => grain.OnActivateAsync(), CancellationToken.None, TaskCreationOptions.None, scheduler).Unwrap();

            Task wrapped = null;
            var wrapperDone = new TaskCompletionSource<bool>();
            var wrappedDone = new TaskCompletionSource<bool>();
            Task<Task> wrapper = new Task<Task>(() =>
            {
                this.output.WriteLine("#0 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                    SynchronizationContext.Current, TaskScheduler.Current);
                
                Task t1 = grain.Test1();

                Action wrappedDoneAction = () => { wrappedDone.SetResult(true); };

                if (bounceToThreadPool)
                {
                    wrapped = t1.ContinueWith(_ => wrappedDoneAction(),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                else
                {
                    wrapped = t1.ContinueWith(_ => wrappedDoneAction());
                }
                wrapperDone.SetResult(true);
                return wrapped;
            });
            wrapper.Start(scheduler);
            await wrapper;
            
            var timeoutLimit = TimeSpan.FromSeconds(1);
            try
            {
                await wrapperDone.Task.WithTimeout(timeoutLimit);
            }
            catch (TimeoutException)
            {
                Assert.True(false, "Result did not arrive before timeout " + timeoutLimit);
            }
            bool done = wrapperDone.Task.Result;

            Assert.True(done, "Wrapper Task finished");
            Assert.True(wrapper.IsCompleted, "Wrapper Task completed");

            //done = wrapped.Wait(TimeSpan.FromSeconds(12));
            //Assert.True(done, "Wrapped Task not timeout");
            await wrapped;
            try
            {
                await wrappedDone.Task.WithTimeout(timeoutLimit);
            }
            catch (TimeoutException)
            {
                Assert.True(false, "Result did not arrive before timeout " + timeoutLimit);
            }
            done = wrappedDone.Task.Result;
            Assert.True(done, "Wrapped Task should be finished");
            Assert.True(wrapped.IsCompleted, "Wrapped Task completed");
        }

        private void LogContext(string what)
        {
            lock (Lockable)
            {
                this.output.WriteLine(
                    "{0}\n"
                    + " TaskScheduler.Current={1}\n"
                    + " Task.Factory.Scheduler={2}\n"
                    + " SynchronizationContext.Current={3}",
                    what,
                    (TaskScheduler.Current == null ? "null" : TaskScheduler.Current.ToString()),
                    (Task.Factory.Scheduler == null ? "null" : Task.Factory.Scheduler.ToString()),
                    (SynchronizationContext.Current == null ? "null" : SynchronizationContext.Current.ToString())
                );

                //var st = new StackTrace();
                //output.WriteLine(st.ToString());
            }
        }

        private class MockSiloDetails : ILocalSiloDetails
        {
            public string DnsHostName { get; }
            public SiloAddress SiloAddress { get; set; }
            public SiloAddress GatewayAddress { get; }
            public string Name { get; set; } = Guid.NewGuid().ToString();
            public string ClusterId { get; }
        }
    }
}
