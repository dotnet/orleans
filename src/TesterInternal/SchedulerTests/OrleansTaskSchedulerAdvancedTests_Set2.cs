using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using UnitTests.Grains;
using UnitTests.TesterInternal;

namespace UnitTests.SchedulerTests
{
    [DeploymentItem("OrleansConfiguration.xml")]
    [TestClass]
    public class OrleansTaskSchedulerAdvancedTests_Set2
    {
        private static readonly object lockable = new object();
        private static readonly int waitFactor = Debugger.IsAttached ? 100 : 1;
        private OrleansTaskScheduler masterScheduler;
        private UnitTestSchedulingContext context;
        private static readonly SafeRandom random = new SafeRandom();

        public OrleansTaskSchedulerAdvancedTests_Set2()
        {
            OrleansTaskSchedulerBasicTests.InitSchedulerLogging();
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            TraceLogger.UnInitialize();
        }

        [TestInitialize]
        public void TestInit()
        {
            context = new UnitTestSchedulingContext();
            masterScheduler = TestInternalHelper.InitializeSchedulerForTesting(context);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            masterScheduler.Stop();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Scheduler")]
        public void ActivationSched_SimpleFifoTest()
        {
            // This is not a great test because there's a 50/50 shot that it will work even if the scheduling
            // is completely and thoroughly broken and both closures are executed "simultaneously"
            TaskScheduler scheduler = masterScheduler.GetWorkItemGroup(context).TaskRunner;

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
            Assert.IsTrue(n != 0, "Work items did not get executed");
            Assert.AreEqual(15, n, "Work items executed out of order");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Scheduler")]
        public void ActivationSched_NewTask_ContinueWith_Wrapped()
        {
            TaskScheduler scheduler = masterScheduler.GetWorkItemGroup(context).TaskRunner;

            Task<Task> wrapped = new Task<Task>(() =>
            {
                Console.WriteLine("#0 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                    SynchronizationContext.Current, TaskScheduler.Current);

                Task t0 = new Task(() =>
                {
                    Console.WriteLine("#1 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.AreEqual(scheduler, TaskScheduler.Current, "TaskScheduler.Current #1");
                });
                Task t1 = t0.ContinueWith(task =>
                {
                    Assert.IsFalse(task.IsFaulted, "Task #1 Faulted=" + task.Exception);

                    Console.WriteLine("#2 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.AreEqual(scheduler, TaskScheduler.Current, "TaskScheduler.Current #2");
                });
                t0.Start(scheduler);
                return t1;
            });
            wrapped.Start(scheduler);
            bool ok = wrapped.Unwrap().Wait(TimeSpan.FromSeconds(2));
            Assert.IsTrue(ok, "Finished OK");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Scheduler")]
        public void ActivationSched_SubTaskExecutionSequencing()
        {
            TaskScheduler scheduler = masterScheduler.GetWorkItemGroup(context).TaskRunner;

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
                        Console.WriteLine("Sub-task " + id + " sleeping");
                        Thread.Sleep(100);
                        Console.WriteLine("Sub-task " + id + " awake");
                        n = k + 1;
                        // ReSharper restore AccessToModifiedClosure
                    })
                    .ContinueWith(tsk =>
                    {
                        LogContext("Sub-task " + id + "-ContinueWith");

                        Console.WriteLine("Sub-task " + id + " Done");
                    });
                }
            };

            Task t = new Task(action);

            t.Start(scheduler);

            // Pause to let things run
            Console.WriteLine("Main-task sleeping");
            Thread.Sleep(TimeSpan.FromSeconds(2));
            Console.WriteLine("Main-task awake");

            // N should be 10, because all tasks should execute serially
            Assert.IsTrue(n != 0, "Work items did not get executed");
            Assert.AreEqual(10, n, "Work items executed concurrently");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task ActivationSched_ContinueWith_1_Test()
        {
            TaskScheduler scheduler = masterScheduler.GetWorkItemGroup(context).TaskRunner;

            var result = new TaskCompletionSource<bool>();
            int n = 0;

            Task wrapper = new Task(() =>
            {
                // ReSharper disable AccessToModifiedClosure
                Task task1 = Task.Factory.StartNew(() => { Console.WriteLine("===> 1a"); Thread.Sleep(1000); n = n + 3; Console.WriteLine("===> 1b"); });
                Task task2 = task1.ContinueWith(task => { n = n * 5; Console.WriteLine("===> 2"); });
                Task task3 = task2.ContinueWith(task => { n = n / 5; Console.WriteLine("===> 3"); });
                Task task4 = task3.ContinueWith(task => { n = n - 2; Console.WriteLine("===> 4"); result.SetResult(true); });
                // ReSharper restore AccessToModifiedClosure
                task4.ContinueWith(task =>
                {
                    Console.WriteLine("Done Faulted={0}", task.IsFaulted);
                    Assert.IsFalse(task.IsFaulted, "Faulted with Exception=" + task.Exception);
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
                Assert.Fail("Result did not arrive before timeout " + timeoutLimit);
            }

            Assert.IsTrue(n != 0, "Work items did not get executed");
            Assert.AreEqual(1, n, "Work items executed out of order");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task ActivationSched_WhenAny()
        {
            TaskScheduler scheduler = masterScheduler.GetWorkItemGroup(context).TaskRunner;

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
                    Console.WriteLine("Task-1 Started");
                    Assert.AreEqual(scheduler, TaskScheduler.Current, "TaskScheduler.Current=" + TaskScheduler.Current);
                    pause1.WaitOne();
                    Console.WriteLine("Task-1 Done");
                    return 1;
                });
                task2 = Task<int>.Factory.StartNew(() =>
                {
                    Console.WriteLine("Task-2 Started");
                    Assert.AreEqual(scheduler, TaskScheduler.Current, "TaskScheduler.Current=" + TaskScheduler.Current);
                    pause2.WaitOne();
                    Console.WriteLine("Task-2 Done");
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
                Assert.Fail("Result did not arrive before timeout " + timeoutLimit);
            }

            pause1.Set();
            await join;
            Assert.IsTrue(join.IsCompleted && !join.IsFaulted, "Join Status " + join.Status);
            Assert.IsFalse(task1.IsFaulted, "Task-1 Faulted " + task1.Exception);
            Assert.IsFalse(task2.IsFaulted, "Task-2 Faulted " + task2.Exception);
            Assert.IsTrue(task1.IsCompleted || task2.IsCompleted, "Task-1 Status = " + task1.Status + " Task-2 Status = " + task2.Status);
            pause2.Set();
            task2.Ignore();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task ActivationSched_WhenAny_Timeout()
        {
            TaskScheduler scheduler = masterScheduler.GetWorkItemGroup(context).TaskRunner;

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
                    Console.WriteLine("Task-1 Started");
                    Assert.AreEqual(scheduler, TaskScheduler.Current, "TaskScheduler.Current=" + TaskScheduler.Current);
                    pause1.WaitOne();
                    Console.WriteLine("Task-1 Done");
                    return 1;
                });
                task2 = Task<int>.Factory.StartNew(() =>
                {
                    Console.WriteLine("Task-2 Started");
                    Assert.AreEqual(scheduler, TaskScheduler.Current, "TaskScheduler.Current=" + TaskScheduler.Current);
                    pause2.WaitOne();
                    Console.WriteLine("Task-2 Done");
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
                Assert.Fail("Result did not arrive before timeout " + timeoutLimit);
            }

            Assert.IsNotNull(join, "Joined promise assigned");
            await join;
            Assert.IsTrue(join.IsCompleted && !join.IsFaulted, "Join Status " + join.Status);
            Assert.IsFalse(task1.IsFaulted, "Task-1 Faulted " + task1.Exception);
            Assert.IsFalse(task1.IsCompleted, "Task-1 Status " + task1.Status);
            Assert.IsFalse(task2.IsFaulted, "Task-2 Faulted " + task2.Exception);
            Assert.IsFalse(task2.IsCompleted, "Task-2 Status " + task2.Status);
            pause1.Set();
            task1.Ignore();
            pause2.Set();
            task2.Ignore();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task ActivationSched_WhenAny_Busy_Timeout()
        {
            TaskScheduler scheduler = masterScheduler.GetWorkItemGroup(context).TaskRunner;

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
                    Console.WriteLine("Task-1 Started");
                    Assert.AreEqual(scheduler, TaskScheduler.Current, "TaskScheduler.Current=" + TaskScheduler.Current);
                    int num1 = 1;
                    while (!pause1.Task.Result) // Infinite busy loop
                    {
                        num1 = random.Next();
                    }
                    Console.WriteLine("Task-1 Done");
                    return num1;
                });
                task2 = Task<int>.Factory.StartNew(() =>
                {
                    Console.WriteLine("Task-2 Started");
                    Assert.AreEqual(scheduler, TaskScheduler.Current, "TaskScheduler.Current=" + TaskScheduler.Current);
                    int num2 = 2;
                    while (!pause2.Task.Result) // Infinite busy loop
                    {
                        num2 = random.Next();
                    }
                    Console.WriteLine("Task-2 Done");
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
                Assert.Fail("Result did not arrive before timeout " + timeoutLimit);
            }

            Assert.IsNotNull(join, "Joined promise assigned");
            await join;
            Assert.IsTrue(join.IsCompleted && !join.IsFaulted, "Join Status " + join.Status);
            Assert.IsFalse(task1.IsFaulted, "Task-1 Faulted " + task1.Exception);
            Assert.IsFalse(task1.IsCompleted, "Task-1 Status " + task1.Status);
            Assert.IsFalse(task2.IsFaulted, "Task-2 Faulted " + task2.Exception);
            Assert.IsFalse(task2.IsCompleted, "Task-2 Status " + task2.Status);
        }


        [TestMethod, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task ActivationSched_Task_Run()
        {
            TaskScheduler scheduler = masterScheduler.GetWorkItemGroup(context).TaskRunner;

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
                    Console.WriteLine("Task-1 Started");
                    Assert.AreNotEqual(scheduler, TaskScheduler.Current, "TaskScheduler.Current=" + TaskScheduler.Current);
                    pause1.WaitOne();
                    Console.WriteLine("Task-1 Done");
                    return 1;
                });
                task2 = Task.Run(() =>
                {
                    Console.WriteLine("Task-2 Started");
                    Assert.AreNotEqual(scheduler, TaskScheduler.Current, "TaskScheduler.Current=" + TaskScheduler.Current);
                    pause2.WaitOne();
                    Console.WriteLine("Task-2 Done");
                    return 2;
                });

                join = Task.WhenAll(task1, task2).ContinueWith(t =>
                {
                    Console.WriteLine("Join Started");
                    if (t.IsFaulted) throw t.Exception;
                    Assert.AreEqual(scheduler, TaskScheduler.Current, "TaskScheduler.Current=" + TaskScheduler.Current);
                    Console.WriteLine("Join Done");
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
                Assert.Fail("Result did not arrive before timeout " + timeoutLimit);
            }

            pause1.Set();
            pause2.Set();
            Assert.IsNotNull(join, "Joined promise assigned");
            await join;
            Assert.IsTrue(join.IsCompleted && !join.IsFaulted, "Join Status " + join);
            Assert.IsTrue(task1.IsCompleted && !task1.IsFaulted, "Task-1 Status " + task1);
            Assert.IsTrue(task2.IsCompleted && !task2.IsFaulted, "Task-2 Status " + task2);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task ActivationSched_Task_Run_Delay()
        {
            TaskScheduler scheduler = masterScheduler.GetWorkItemGroup(context).TaskRunner;

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
                    Console.WriteLine("Task-1 Started");
                    Assert.AreNotEqual(scheduler, TaskScheduler.Current, "Before Task.Delay TaskScheduler.Current=" + TaskScheduler.Current);
                    Task.Delay(1);
                    Assert.AreNotEqual(scheduler, TaskScheduler.Current, "After Task.Delay TaskScheduler.Current=" + TaskScheduler.Current);
                    pause1.WaitOne();
                    Console.WriteLine("Task-1 Done");
                    return 1;
                });
                task2 = Task.Run(() =>
                {
                    Console.WriteLine("Task-2 Started");
                    Assert.AreNotEqual(scheduler, TaskScheduler.Current, "Before Task.Delay TaskScheduler.Current=" + TaskScheduler.Current);
                    Task.Delay(1);
                    Assert.AreNotEqual(scheduler, TaskScheduler.Current, "After Task.Delay TaskScheduler.Current=" + TaskScheduler.Current);
                    pause2.WaitOne();
                    Console.WriteLine("Task-2 Done");
                    return 2;
                });

                join = Task.WhenAll(task1, task2).ContinueWith(t =>
                {
                    Console.WriteLine("Join Started");
                    if (t.IsFaulted) throw t.Exception;
                    Assert.AreEqual(scheduler, TaskScheduler.Current, "TaskScheduler.Current=" + TaskScheduler.Current);
                    Console.WriteLine("Join Done");
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
                Assert.Fail("Result did not arrive before timeout " + timeoutLimit);
            }

            pause1.Set();
            pause2.Set();
            Assert.IsNotNull(join, "Joined promise assigned");
            await join;
            Assert.IsTrue(join.IsCompleted && !join.IsFaulted, "Join Status " + join);
            Assert.IsTrue(task1.IsCompleted && !task1.IsFaulted, "Task-1 Status " + task1);
            Assert.IsTrue(task2.IsCompleted && !task2.IsFaulted, "Task-2 Status " + task2);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task ActivationSched_Task_Delay()
        {
            TaskScheduler scheduler = masterScheduler.GetWorkItemGroup(context).TaskRunner;

            Task wrapper = new Task(async () =>
            {
                Assert.AreEqual(scheduler, TaskScheduler.Current, "Before Task.Delay #1 TaskScheduler.Current=" + TaskScheduler.Current);
                await DoDelay(1);
                Assert.AreEqual(scheduler, TaskScheduler.Current, "After Task.Delay #1 TaskScheduler.Current=" + TaskScheduler.Current);
                await DoDelay(2);
                Assert.AreEqual(scheduler, TaskScheduler.Current, "After Task.Delay #2 TaskScheduler.Current=" + TaskScheduler.Current);
            });
            wrapper.Start(scheduler);

            await wrapper;
        }

        private static async Task DoDelay(int i)
        {
            try
            {
                Console.WriteLine("Before Task.Delay #{0} TaskScheduler.Current={1}", i, TaskScheduler.Current);
                await Task.Delay(1);
                Console.WriteLine("After Task.Delay #{0} TaskScheduler.Current={1}", i, TaskScheduler.Current);
            }
            catch (ObjectDisposedException)
            {
                // Ignore any problems with ObjectDisposedException if console output stream has already been closed
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task ActivationSched_Turn_Execution_Order_Loop()
        {
            TaskScheduler scheduler = masterScheduler.GetWorkItemGroup(context).TaskRunner;

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
                int sleepTime = TestConstants.random.Next(100);
                resultHandles[i] = new TaskCompletionSource<bool>();
                taskChains[i] = new Task(() =>
                {
                    const int taskNum = 0;
                    try
                    {
                        Assert.AreEqual(-1, executingGlobal, "Detected unexpected other execution in chain " + chainNum + " Task " + taskNum);
                        Assert.IsFalse(executingChain[chainNum], "Detected unexpected other execution on chain " + chainNum + " Task " + taskNum);

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
                        Console.WriteLine("Inside Chain {0} Task {1}", chainNum, taskNum);
                        try
                        {
                            Assert.AreEqual(-1, executingGlobal, "Detected unexpected other execution in chain " + chainNum + " Task " + taskNum);
                            Assert.IsFalse(executingChain[chainNum], "Detected unexpected other execution on chain " + chainNum + " Task " + taskNum);
                            Assert.AreEqual(taskNum - 1, stageComplete[chainNum], "Detected unexpected execution stage on chain " + chainNum + " Task " + taskNum);

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
                    Console.WriteLine("Inside Chain {0} Final Task", chainNum);
                    resultHandles[chainNum].SetResult(true);
                }, scheduler);
            }

            for (int i = 0; i < NumChains; i++)
            {
                taskChains[i].Start(scheduler);
            }

            for (int i = 0; i < NumChains; i++)
            {
                TimeSpan waitCheckTime = TimeSpan.FromMilliseconds(150 * ChainLength * NumChains * waitFactor);

                var timeoutLimit = TimeSpan.FromSeconds(1);
                try
                {
                    await resultHandles[i].Task.WithTimeout(waitCheckTime);
                }
                catch (TimeoutException)
                {
                    Assert.Fail("Result did not arrive before timeout " + timeoutLimit);
                }

                bool ok = resultHandles[i].Task.Result;
                Assert.IsFalse(taskChainEnds[i].IsFaulted, "Task chain " + i + " should not be Faulted: " + taskChainEnds[i].Exception);
                Assert.IsTrue(taskChainEnds[i].IsCompleted, "Task chain " + i + " should be completed");
                Assert.AreEqual(ChainLength - 1, stageComplete[i], "Task chain " + i + " should have completed all stages");
                Assert.IsTrue(ok, "Successfully waited for ResultHandle for Task chain " + i);
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task ActivationSched_Test1()
        {
            TaskScheduler scheduler = masterScheduler.GetWorkItemGroup(context).TaskRunner;

            await Run_ActivationSched_Test1(scheduler, false);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task ActivationSched_Test1_Bounce()
        {
            TaskScheduler scheduler = masterScheduler.GetWorkItemGroup(context).TaskRunner;

            await Run_ActivationSched_Test1(scheduler, true);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task OrleansSched_Test1()
        {
            UnitTestSchedulingContext context = new UnitTestSchedulingContext();
            OrleansTaskScheduler orleansTaskScheduler = TestInternalHelper.InitializeSchedulerForTesting(context);
            ActivationTaskScheduler scheduler = orleansTaskScheduler.GetWorkItemGroup(context).TaskRunner;

            await Run_ActivationSched_Test1(scheduler, false);
        }
        [TestMethod, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task OrleansSched_Test1_Bounce()
        {
            UnitTestSchedulingContext context = new UnitTestSchedulingContext();
            OrleansTaskScheduler orleansTaskScheduler = TestInternalHelper.InitializeSchedulerForTesting(context);
            ActivationTaskScheduler scheduler = orleansTaskScheduler.GetWorkItemGroup(context).TaskRunner;

            await Run_ActivationSched_Test1(scheduler, true);
        }

        internal static async Task Run_ActivationSched_Test1(TaskScheduler scheduler, bool bounceToThreadPool)
        {
            var grainId = GrainId.GetGrainId(0, Guid.NewGuid());
            var grain = new NonReentrentStressGrainWithoutState( grainId, new GrainRuntime(Guid.NewGuid(), null, null, null, null, null, null));
            await grain.OnActivateAsync();

            Task wrapped = null;
            var wrapperDone = new TaskCompletionSource<bool>();
            var wrappedDone = new TaskCompletionSource<bool>();
            Task<Task> wrapper = new Task<Task>(() =>
            {
                Console.WriteLine("#0 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
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
                Assert.Fail("Result did not arrive before timeout " + timeoutLimit);
            }
            bool done = wrapperDone.Task.Result;

            Assert.IsTrue(done, "Wrapper Task finished");
            Assert.IsTrue(wrapper.IsCompleted, "Wrapper Task completed");

            //done = wrapped.Wait(TimeSpan.FromSeconds(12));
            //Assert.IsTrue(done, "Wrapped Task not timeout");
            await wrapped;
            try
            {
                await wrappedDone.Task.WithTimeout(timeoutLimit);
            }
            catch (TimeoutException)
            {
                Assert.Fail("Result did not arrive before timeout " + timeoutLimit);
            }
            done = wrappedDone.Task.Result;
            Assert.IsTrue(done, "Wrapped Task should be finished");
            Assert.IsTrue(wrapped.IsCompleted, "Wrapped Task completed");
        }

        private static void LogContext(string what)
        {
            lock (lockable)
            {
                Console.WriteLine(
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
                //Console.WriteLine(st.ToString());
            }
        }
    }
}
