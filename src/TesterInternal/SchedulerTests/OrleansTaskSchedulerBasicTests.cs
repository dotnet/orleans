using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Schedulers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Scheduler;
using UnitTests.TesterInternal;

// ReSharper disable ConvertToConstant.Local

namespace UnitTests.SchedulerTests
{
    internal class UnitTestSchedulingContext : ISchedulingContext
    {
        public SchedulingContextType ContextType { get { return SchedulingContextType.Activation; } }

        public string Name { get { return "UnitTestSchedulingContext"; } }

        public bool IsSystemPriorityContext { get { return false; } }

        public string DetailedStatus() { return ToString(); }

        #region IEquatable<ISchedulingContext> Members

        public bool Equals(ISchedulingContext other)
        {
            return base.Equals(other);
        }

        #endregion
    }

    [TestClass]
    [DeploymentItem("OrleansConfiguration.xml")]
    [DeploymentItem("ClientConfiguration.xml")]
    public class OrleansTaskSchedulerBasicTests 
    {
        private static readonly object lockable = new object();

        [TestInitialize]
        public void MyTestInitialize()
        {
            SynchronizationContext.SetSynchronizationContext(null);
            InitSchedulerLogging();
        }

        [TestCleanup]
        public void MyTestCleanup()
        {
            SynchronizationContext.SetSynchronizationContext(null);
            TraceLogger.SetTraceLevelOverrides(new List<Tuple<string, Severity>>()); // Reset Log level overrides
            //TraceLogger.UnInitialize();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
        public void Async_Task_Start_OrleansTaskScheduler()
        {
            InitSchedulerLogging();
            UnitTestSchedulingContext cntx = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(cntx);

            int expected = 2;
            bool done = false;
            Task<int> t = new Task<int>(() => { done = true; return expected; });
            t.Start(scheduler);

            int received = t.Result;
            Assert.IsTrue(t.IsCompleted, "Task should have completed");
            Assert.IsFalse(t.IsFaulted, "Task should not thrown exception: " + t.Exception);
            Assert.IsTrue(done, "Task should be done");
            Assert.AreEqual(expected, received, "Task did not return expected value " + expected);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
        public void Async_Task_Start_ActivationTaskScheduler()
        {
            InitSchedulerLogging();
            UnitTestSchedulingContext cntx = new UnitTestSchedulingContext();
            OrleansTaskScheduler masterScheduler = TestInternalHelper.InitializeSchedulerForTesting(cntx);
            ActivationTaskScheduler activationScheduler = masterScheduler.GetWorkItemGroup(cntx).TaskRunner;

            int expected = 2;
            bool done = false;
            Task<int> t = new Task<int>(() => { done = true; return expected; });
            t.Start(activationScheduler);

            int received = t.Result;
            Assert.IsTrue(t.IsCompleted, "Task should have completed");
            Assert.IsFalse(t.IsFaulted, "Task should not thrown exception: " + t.Exception);
            Assert.IsTrue(done, "Task should be done");
            Assert.AreEqual(expected, received, "Task did not return expected value " + expected);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Scheduler")]
        public void Sched_SimpleFifoTest()
        {
            // This is not a great test because there's a 50/50 shot that it will work even if the scheduling
            // is completely and thoroughly broken and both closures are executed "simultaneously"
            UnitTestSchedulingContext context = new UnitTestSchedulingContext();
            OrleansTaskScheduler orleansTaskScheduler = TestInternalHelper.InitializeSchedulerForTesting(context);
            ActivationTaskScheduler scheduler = orleansTaskScheduler.GetWorkItemGroup(context).TaskRunner;

            int n = 0;
            // ReSharper disable AccessToModifiedClosure
            IWorkItem item1 = new ClosureWorkItem(() => { n = n + 5; });
            IWorkItem item2 = new ClosureWorkItem(() => { n = n * 3; });
            // ReSharper restore AccessToModifiedClosure
            orleansTaskScheduler.QueueWorkItem(item1, context);
            orleansTaskScheduler.QueueWorkItem(item2, context);

            // Pause to let things run
            Thread.Sleep(1000);

            // N should be 15, because the two tasks should execute in order
            Assert.IsTrue(n != 0, "Work items did not get executed");
            Assert.AreEqual(15, n, "Work items executed out of order");
            Console.WriteLine("Test executed OK.");
            orleansTaskScheduler.Stop();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Scheduler")]
        public void Sched_Task_TplFifoTest()
        {
            // This is not a great test because there's a 50/50 shot that it will work even if the scheduling
            // is completely and thoroughly broken and both closures are executed "simultaneously"
            UnitTestSchedulingContext context = new UnitTestSchedulingContext();
            OrleansTaskScheduler orleansTaskScheduler = TestInternalHelper.InitializeSchedulerForTesting(context);
            ActivationTaskScheduler scheduler = orleansTaskScheduler.GetWorkItemGroup(context).TaskRunner;

            int n = 0;

            // ReSharper disable AccessToModifiedClosure
            Task task1 = new Task(() => { Thread.Sleep(1000); n = n + 5; });
            Task task2 = new Task(() => { n = n * 3; });
            // ReSharper restore AccessToModifiedClosure

            task1.Start(scheduler);
            task2.Start(scheduler);

            // Pause to let things run
            Thread.Sleep(2000);

            // N should be 15, because the two tasks should execute in order
            Assert.IsTrue(n != 0, "Work items did not get executed");
            Assert.AreEqual(15, n, "Work items executed out of order");
            Console.WriteLine("Test executed OK.");
            orleansTaskScheduler.Stop();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_Task_TplFifoTest_TaskScheduler()
        {
            UnitTestSchedulingContext cntx = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(cntx);
            ActivationTaskScheduler activationScheduler = scheduler.GetWorkItemGroup(cntx).TaskRunner;

            int n = 0;

            // ReSharper disable AccessToModifiedClosure
            Task task1 = new Task(() => { Thread.Sleep(1000); n = n + 5; });
            Task task2 = new Task(() => { n = n * 3; });
            // ReSharper restore AccessToModifiedClosure

            // By queuuing to ActivationTaskScheduler we guarantee single threaded ordered execution.
            // If we queued to OrleansTaskScheduler we would not guarantee that.
            task1.Start(activationScheduler);
            task2.Start(activationScheduler);

            // Pause to let things run
            Thread.Sleep(TimeSpan.FromSeconds(2));

            // N should be 15, because the two tasks should execute in order
            Assert.IsTrue(n != 0, "Work items did not get executed");
            Assert.AreEqual(15, n, "Work items executed out of order");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_Task_StartTask_1()
        {
            UnitTestSchedulingContext cntx = new UnitTestSchedulingContext();;
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(cntx);

            ManualResetEvent pause1 = new ManualResetEvent(false);
            ManualResetEvent pause2 = new ManualResetEvent(false);
            Task task1 = new Task(() => { pause1.WaitOne(); Console.WriteLine("Task-1"); });
            Task task2 = new Task(() => { pause2.WaitOne(); Console.WriteLine("Task-2"); });

            task1.Start(scheduler);
            task2.Start(scheduler);

            pause1.Set();
            bool ok = task1.Wait(TimeSpan.FromMilliseconds(100));
            if (!ok) throw new TimeoutException();
            Assert.IsTrue(task1.IsCompleted, "Task.IsCompleted-1");
            Assert.IsFalse(task1.IsFaulted, "Task.IsFaulted-1");

            pause2.Set();
            ok = task2.Wait(TimeSpan.FromMilliseconds(100));
            if (!ok) throw new TimeoutException();
            Assert.IsTrue(task2.IsCompleted, "Task.IsCompleted-2");
            Assert.IsFalse(task2.IsFaulted, "Task.IsFaulted-2");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_Task_StartTask_2()
        {
            UnitTestSchedulingContext cntx = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(cntx);

            ManualResetEvent pause1 = new ManualResetEvent(false);
            ManualResetEvent pause2 = new ManualResetEvent(false);
            Task task1 = new Task(() => { pause1.WaitOne(); Console.WriteLine("Task-1"); });
            Task task2 = new Task(() => { pause2.WaitOne(); Console.WriteLine("Task-2"); });

            pause1.Set();
            task1.Start(scheduler);

            bool ok = task1.Wait(TimeSpan.FromMilliseconds(100));
            if (!ok) throw new TimeoutException();

            Assert.IsTrue(task1.IsCompleted, "Task.IsCompleted-1");
            Assert.IsFalse(task1.IsFaulted, "Task.IsFaulted-1");

            task2.Start(scheduler);
            pause2.Set();
            ok = task2.Wait(TimeSpan.FromMilliseconds(100));
            if (!ok) throw new TimeoutException();

            Assert.IsTrue(task2.IsCompleted, "Task.IsCompleted-2");
            Assert.IsFalse(task2.IsFaulted, "Task.IsFaulted-2");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_Task_StartTask_Wrapped()
        {
            UnitTestSchedulingContext cntx = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(cntx);

            ManualResetEvent pause1 = new ManualResetEvent(false);
            ManualResetEvent pause2 = new ManualResetEvent(false);
            Task task1 = new Task(() => { pause1.WaitOne(); Console.WriteLine("Task-1"); });
            Task task2 = new Task(() => { pause2.WaitOne(); Console.WriteLine("Task-2"); });

            Task wrapper1 = new Task(() =>
            {
                task1.Start(scheduler);
                bool ok = task1.Wait(TimeSpan.FromMilliseconds(100));
                if (!ok) throw new TimeoutException();
            });
            Task wrapper2 = new Task(() =>
            {
                task2.Start(scheduler);
                bool ok = task2.Wait(TimeSpan.FromMilliseconds(100));
                if (!ok) throw new TimeoutException();
            });

            pause1.Set();
            wrapper1.Start(scheduler);
            bool ok1 = wrapper1.Wait(TimeSpan.FromMilliseconds(1000));
            if (!ok1) throw new TimeoutException();

            Assert.IsTrue(task1.IsCompleted, "Task.IsCompleted-1");
            Assert.IsFalse(task1.IsFaulted, "Task.IsFaulted-1");

            wrapper2.Start(scheduler);
            pause2.Set();
            bool finished = wrapper2.Wait(TimeSpan.FromMilliseconds(100));
            if (!finished) throw new TimeoutException();

            Assert.IsTrue(task2.IsCompleted, "Task.IsCompleted-2");
            Assert.IsFalse(task2.IsFaulted, "Task.IsFaulted-2");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_Task_StartTask_Wait_Wrapped()
        {
            UnitTestSchedulingContext cntx = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(cntx);

            const int NumTasks = 100;

            ManualResetEvent[] flags = new ManualResetEvent[NumTasks];
            for (int i = 0; i < NumTasks; i++)
            {
                flags[i] = new ManualResetEvent(false);
            }

            Task[] tasks = new Task[NumTasks];
            for (int i = 0; i < NumTasks; i++)
            {
                int taskNum = i; // Capture
                tasks[i] = new Task(() => { Console.WriteLine("Inside Task-" + taskNum); flags[taskNum].WaitOne(); });
                Console.WriteLine("Created Task-" + taskNum + " Id=" + tasks[taskNum].Id);
            }

            Task[] wrappers = new Task[NumTasks];
            for (int i = 0; i < NumTasks; i++)
            {
                int taskNum = i; // Capture
                wrappers[i] = new Task(() =>
                {
                    Console.WriteLine("Inside Wrapper-" + taskNum); 
                    tasks[taskNum].Start(scheduler);
                });
                wrappers[i].ContinueWith(t =>
                {
                    Assert.IsFalse(t.IsFaulted, "Warpper.IsFaulted-" + taskNum + " " + t.Exception);
                    Assert.IsTrue(t.IsCompleted, "Wrapper.IsCompleted-" + taskNum);
                });
                Console.WriteLine("Created Wrapper-" + taskNum + " Task.Id=" + wrappers[taskNum].Id);
            }

            foreach (var wrapper in wrappers) wrapper.Start(scheduler);
            foreach (var flag in flags) flag.Set();
            for (int i = 0; i < wrappers.Length; i++)
            {
                bool ok = wrappers[i].Wait(TimeSpan.FromMilliseconds(NumTasks * 150 * 2));
                Assert.IsTrue(ok, "Wait completed successfully for Wrapper-" + i);
            }

            for (int i = 0; i < tasks.Length; i++)
            {
                bool ok = tasks[i].Wait(TimeSpan.FromMilliseconds(NumTasks * 150 * 2));
                Assert.IsTrue(ok, "Wait completed successfully for Task-" + i);
                Assert.IsFalse(tasks[i].IsFaulted, "Task.IsFaulted-" + i + " " + tasks[i].Exception);
                Assert.IsTrue(tasks[i].IsCompleted, "Task.IsCompleted-" + i);
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_Task_ClosureWorkItem_Wait()
        {
            UnitTestSchedulingContext cntx = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(cntx);

            const int NumTasks = 10;

            ManualResetEvent[] flags = new ManualResetEvent[NumTasks];
            for (int i = 0; i < NumTasks; i++)
            {
                flags[i] = new ManualResetEvent(false);
            }

            Task[] tasks = new Task[NumTasks];
            for (int i = 0; i < NumTasks; i++)
            {
                int taskNum = i; // Capture
                tasks[i] = new Task(() => { Console.WriteLine("Inside Task-" + taskNum); flags[taskNum].WaitOne(); });
            }

            ClosureWorkItem[] workItems = new ClosureWorkItem[NumTasks];
            for (int i = 0; i < NumTasks; i++)
            {
                int taskNum = i; // Capture
                workItems[i] = new ClosureWorkItem(() =>
                {
                    Console.WriteLine("Inside ClosureWorkItem-" + taskNum);
                    tasks[taskNum].Start(scheduler);
                    bool ok = tasks[taskNum].Wait(TimeSpan.FromMilliseconds(NumTasks * 100));
                    Assert.IsTrue(ok, "Wait completed successfully inside ClosureWorkItem-" + taskNum);
                });
            }

            foreach (var workItem in workItems) scheduler.QueueWorkItem(workItem, cntx);
            foreach (var flag in flags) flag.Set();
            for (int i = 0; i < tasks.Length; i++)
            {
                bool ok = tasks[i].Wait(TimeSpan.FromMilliseconds(NumTasks * 150));
                Assert.IsTrue(ok, "Wait completed successfully for Task-" + i);
            }


            for (int i = 0; i < tasks.Length; i++)
            {
                Assert.IsFalse(tasks[i].IsFaulted, "Task.IsFaulted-" + i + " Exception=" + tasks[i].Exception);
                Assert.IsTrue(tasks[i].IsCompleted, "Task.IsCompleted-" + i);
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Scheduler")]
        public void Sched_Task_TaskWorkItem_CurrentScheduler()
        {
            UnitTestSchedulingContext context = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(context);
            ActivationTaskScheduler activationScheduler = scheduler.GetWorkItemGroup(context).TaskRunner;

            var result0 = new TaskCompletionSource<bool>();
            var result1 = new TaskCompletionSource<bool>();

            Task t1 = null;
            scheduler.QueueWorkItem(new ClosureWorkItem(() =>
            {
                try
                {
                    Console.WriteLine("#0 - TaskWorkItem - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.AreEqual(activationScheduler, TaskScheduler.Current, "TaskScheduler.Current #0");

                    t1 = new Task(() =>
                    {
                        Console.WriteLine("#1 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                            SynchronizationContext.Current, TaskScheduler.Current);
                        Assert.AreEqual(activationScheduler, TaskScheduler.Current, "TaskScheduler.Current #1");
                        result1.SetResult(true);
                    });
                    t1.Start();

                    result0.SetResult(true);
                }
                catch (Exception exc)
                {
                    result0.SetException(exc);
                }
            }), context);

            result0.Task.Wait(TimeSpan.FromSeconds(1));
            Assert.IsTrue(result0.Task.Exception == null, "Task-0 should not throw exception: " + result0.Task.Exception);
            Assert.IsTrue(result0.Task.Result, "Task-0 completed");

            Assert.IsNotNull(t1, "Task-1 started");
            result1.Task.Wait(TimeSpan.FromSeconds(1));
            Assert.IsTrue(t1.IsCompleted, "Task-1 completed");
            Assert.IsFalse(t1.IsFaulted, "Task-1 faulted: " + t1.Exception);
            Assert.IsTrue(result1.Task.Result, "Task-1 completed");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Scheduler")]
        public void Sched_Task_ClosureWorkItem_SpecificScheduler()
        {
            UnitTestSchedulingContext context = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(context);
            ActivationTaskScheduler activationScheduler = scheduler.GetWorkItemGroup(context).TaskRunner;

            var result0 = new TaskCompletionSource<bool>();
            var result1 = new TaskCompletionSource<bool>();

            Task t1 = null;
            scheduler.QueueWorkItem(new ClosureWorkItem(() =>
            {
                try
                {
                    Console.WriteLine("#0 - TaskWorkItem - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.AreEqual(activationScheduler, TaskScheduler.Current, "TaskScheduler.Current #0");

                    t1 = new Task(() =>
                    {
                        Console.WriteLine("#1 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                            SynchronizationContext.Current, TaskScheduler.Current);
                        Assert.AreEqual(scheduler, TaskScheduler.Current, "TaskScheduler.Current #1");
                        result1.SetResult(true);
                    });
                    t1.Start(scheduler);

                    result0.SetResult(true);
                }
                catch (Exception exc)
                {
                    result0.SetException(exc);
                }
            }), context);

            result0.Task.Wait(TimeSpan.FromSeconds(1));
            Assert.IsTrue(result0.Task.Exception == null, "Task-0 should not throw exception: " + result0.Task.Exception);
            Assert.IsTrue(result0.Task.Result, "Task-0 completed");

            Assert.IsNotNull(t1, "Task-1 started");
            result1.Task.Wait(TimeSpan.FromSeconds(1));
            Assert.IsTrue(t1.IsCompleted, "Task-1 completed");
            Assert.IsFalse(t1.IsFaulted, "Task-1 faulted: " + t1.Exception);
            Assert.IsTrue(result1.Task.Result, "Task-1 completed");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_Task_NewTask_ContinueWith_Wrapped()
        {
            TaskScheduler scheduler = new QueuedTaskScheduler();

            Task wrapped = new Task(() =>
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
                bool ok = t1.Wait(TimeSpan.FromSeconds(15));
                if (!ok) throw new TimeoutException();
            });
            wrapped.Start(scheduler);
            bool finished = wrapped.Wait(TimeSpan.FromSeconds(30));
            if (!finished) throw new TimeoutException();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_Task_NewTask_ContinueWith_Wrapped_OrleansTaskScheduler()
        {
            UnitTestSchedulingContext rootContext = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(rootContext);

            Task wrapped = new Task(() =>
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
                bool ok = t1.Wait(TimeSpan.FromSeconds(15));
                if (!ok) throw new TimeoutException();
            });
            wrapped.Start(scheduler);
            bool finished = wrapped.Wait(TimeSpan.FromSeconds(30));
            if (!finished) throw new TimeoutException();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_Task_NewTask_ContinueWith_TaskScheduler()
        {
            UnitTestSchedulingContext rootContext = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(rootContext);

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
            }, scheduler);
            t0.Start(scheduler);
            bool ok = t1.Wait(TimeSpan.FromSeconds(30));
            if (!ok) throw new TimeoutException();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_Task_StartNew_ContinueWith_TaskScheduler()
        {
            UnitTestSchedulingContext rootContext = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(rootContext);

            Console.WriteLine("#0 - StartNew - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                SynchronizationContext.Current, TaskScheduler.Current);

            Task t0 = Task.Factory.StartNew(state =>
            {
                Console.WriteLine("#1 - StartNew - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                    SynchronizationContext.Current, TaskScheduler.Current);
                Assert.AreEqual(scheduler, TaskScheduler.Current, "TaskScheduler.Current #1");
            }, null, CancellationToken.None, TaskCreationOptions.None, scheduler);
            Task t1 = t0.ContinueWith(task =>
            {
                Assert.IsFalse(task.IsFaulted, "Task #1 Faulted=" + task.Exception);

                Console.WriteLine("#2 - StartNew - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                    SynchronizationContext.Current, TaskScheduler.Current);
                Assert.AreEqual(scheduler, TaskScheduler.Current, "TaskScheduler.Current #2");
            }, scheduler);
            bool ok = t1.Wait(TimeSpan.FromSeconds(30));
            if (!ok) throw new TimeoutException();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_Task_SubTaskExecutionSequencing()
        {
            UnitTestSchedulingContext rootContext = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(rootContext);

            UnitTestSchedulingContext context = new UnitTestSchedulingContext();
            scheduler.RegisterWorkContext(context);

            LogContext("Main-task " + Task.CurrentId);

            int n = 0;

            Action closure = () =>
            {
                LogContext("ClosureWorkItem-task " + Task.CurrentId);

                for (int i = 0; i < 10; i++)
                {
                    int id = -1;
                    Action action = () =>
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
                    };
                    Task.Factory.StartNew(action).ContinueWith(tsk =>
                    {
                        LogContext("Sub-task " + id + "-ContinueWith");

                        Console.WriteLine("Sub-task " + id + " Done");
                    });
                }
            };

            IWorkItem workItem = new ClosureWorkItem(closure);

            scheduler.QueueWorkItem(workItem, context);

            // Pause to let things run
            Console.WriteLine("Main-task sleeping");
            Thread.Sleep(TimeSpan.FromSeconds(2));
            Console.WriteLine("Main-task awake");

            // N should be 10, because all tasks should execute serially
            Assert.IsTrue(n != 0, "Work items did not get executed");
            Assert.AreEqual(10, n, "Work items executed concurrently");
            scheduler.Stop();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_Task_RequestContext_NewTask_ContinueWith()
        {
            UnitTestSchedulingContext rootContext = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(rootContext);

            const string key = "K";
            int val = TestConstants.random.Next();
            RequestContext.Set(key, val);

            Console.WriteLine("Initial - SynchronizationContext.Current={0} TaskScheduler.Current={1} Thread={2}",
                SynchronizationContext.Current, TaskScheduler.Current, Thread.CurrentThread.ManagedThreadId);

            Assert.AreEqual(val, RequestContext.Get(key), "RequestContext.Get Initial");

            Task t0 = new Task(() =>
            {
                Console.WriteLine("#0 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1} Thread={2}",
                    SynchronizationContext.Current, TaskScheduler.Current, Thread.CurrentThread.ManagedThreadId);

                Assert.AreEqual(val, RequestContext.Get(key), "RequestContext.Get #0");

                Task t1 = new Task(() =>
                {
                    Console.WriteLine("#1 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1} Thread={2}",
                        SynchronizationContext.Current, TaskScheduler.Current, Thread.CurrentThread.ManagedThreadId);
                    Assert.AreEqual(val, RequestContext.Get(key), "RequestContext.Get #1");
                });
                Task t2 = t1.ContinueWith(task =>
                {
                    Assert.IsFalse(task.IsFaulted, "Task #1 FAULTED=" + task.Exception);

                    Console.WriteLine("#2 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1} Thread={2}",
                        SynchronizationContext.Current, TaskScheduler.Current, Thread.CurrentThread.ManagedThreadId);
                    Assert.AreEqual(val, RequestContext.Get(key), "RequestContext.Get #2");
                });
                t1.Start(scheduler);
                bool ok = t2.Wait(TimeSpan.FromSeconds(5));
                if (!ok) throw new TimeoutException();
            });
            t0.Start(scheduler);
            bool finished = t0.Wait(TimeSpan.FromSeconds(10));
            if (!finished) throw new TimeoutException();
            Assert.IsFalse(t0.IsFaulted, "Task #0 FAULTED=" + t0.Exception);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_AC_RequestContext_StartNew_ContinueWith()
        {
            UnitTestSchedulingContext rootContext = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(rootContext);

            const string key = "A";
            int val = TestConstants.random.Next();
            RequestContext.Set(key, val);

            Console.WriteLine("Initial - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                SynchronizationContext.Current, TaskScheduler.Current);

            Assert.AreEqual(val, RequestContext.Get(key), "RequestContext.Get Initial");

            Task t0 = Task.Factory.StartNew(() =>
            {
                Console.WriteLine("#0 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                    SynchronizationContext.Current, TaskScheduler.Current);

                Assert.AreEqual(val, RequestContext.Get(key), "RequestContext.Get #0");

                Task t1 = Task.Factory.StartNew(() =>
                {
                    Console.WriteLine("#1 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.AreEqual(val, RequestContext.Get(key), "RequestContext.Get #1");
                });
                Task t2 = t1.ContinueWith((_) =>
                {
                    Console.WriteLine("#2 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.AreEqual(val, RequestContext.Get(key), "RequestContext.Get #2");
                });
                t2.Wait(TimeSpan.FromSeconds(5));
            });
            t0.Wait(TimeSpan.FromSeconds(10));
            Assert.IsTrue(t0.IsCompleted, "Task #0 FAULTED=" + t0.Exception);
        }

        private static void LogContext(string what)
        {
            lock (lockable)
            {
                Console.WriteLine(
                    "{0}\n"
                    + " TaskScheduler.Current={1}\n"
                    + " Task.Factory.Scheduler={2}\n"
                    + " SynchronizationContext.Current={3}\n"
                    + " Orleans-RuntimeContext.Current={4}",
                    what,
                    (TaskScheduler.Current == null ? "null" : TaskScheduler.Current.ToString()),
                    (Task.Factory.Scheduler == null ? "null" : Task.Factory.Scheduler.ToString()),
                    (SynchronizationContext.Current == null ? "null" : SynchronizationContext.Current.ToString()),
                    (RuntimeContext.Current == null ? "null" : RuntimeContext.Current.ToString())
                );

                //var st = new StackTrace();
                //Console.WriteLine("Backtrace: " + st);
            }
        }

        internal static void InitSchedulerLogging()
        {
            TraceLogger.UnInitialize();
            //TraceLogger.LogConsumers.Add(new LogWriterToConsole());
            if (!Logger.TelemetryConsumers.OfType<ConsoleTelemetryConsumer>().Any())
            {
                Logger.TelemetryConsumers.Add(new ConsoleTelemetryConsumer());
            }

            var traceLevels = new[]
            {
                Tuple.Create("Scheduler", Severity.Verbose3),
                Tuple.Create("Scheduler.WorkerPoolThread", Severity.Verbose2),
            };
            TraceLogger.SetTraceLevelOverrides(new List<Tuple<string, Severity>>(traceLevels));

            var orleansConfig = new ClusterConfiguration();
            orleansConfig.StandardLoad();
            NodeConfiguration config = orleansConfig.GetConfigurationForNode("Primary");
            StatisticsCollector.Initialize(config);
            SchedulerStatisticsGroup.Init();
        }
    }
}

// ReSharper restore ConvertToConstant.Local
