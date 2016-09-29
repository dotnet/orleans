﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Scheduler;
using UnitTests.TesterInternal;
using Xunit;
using Xunit.Abstractions;

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
    
    public class OrleansTaskSchedulerBasicTests : IDisposable
    {
        private readonly ITestOutputHelper output;
        private static readonly object lockable = new object();
        
        public OrleansTaskSchedulerBasicTests(ITestOutputHelper output)
        {
            this.output = output;
            SynchronizationContext.SetSynchronizationContext(null);
            InitSchedulerLogging();
        }
        
        public void Dispose()
        {
            SynchronizationContext.SetSynchronizationContext(null);
            LogManager.SetTraceLevelOverrides(new List<Tuple<string, Severity>>()); // Reset Log level overrides
            //LogManager.UnInitialize();
        }

        [Fact, TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
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
            Assert.True(t.IsCompleted, "Task should have completed");
            Assert.False(t.IsFaulted, "Task should not thrown exception: " + t.Exception);
            Assert.True(done, "Task should be done");
            Assert.Equal(expected, received);      
        }

        [Fact, TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
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
            Assert.True(t.IsCompleted, "Task should have completed");
            Assert.False(t.IsFaulted, "Task should not thrown exception: " + t.Exception);
            Assert.True(done, "Task should be done");
            Assert.Equal(expected, received);
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
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
            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(15, n);
            output.WriteLine("Test executed OK.");
            orleansTaskScheduler.Stop();
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
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
            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(15, n);
            output.WriteLine("Test executed OK.");
            orleansTaskScheduler.Stop();
        }

        [Fact, TestCategory("Functional"), TestCategory("TaskScheduler")]
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
            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(15, n);
        }

        [Fact, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_Task_StartTask_1()
        {
            UnitTestSchedulingContext cntx = new UnitTestSchedulingContext();;
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(cntx);

            ManualResetEvent pause1 = new ManualResetEvent(false);
            ManualResetEvent pause2 = new ManualResetEvent(false);
            Task task1 = new Task(() => { pause1.WaitOne(); output.WriteLine("Task-1"); });
            Task task2 = new Task(() => { pause2.WaitOne(); output.WriteLine("Task-2"); });

            task1.Start(scheduler);
            task2.Start(scheduler);

            pause1.Set();
            bool ok = task1.Wait(TimeSpan.FromMilliseconds(100));
            if (!ok) throw new TimeoutException();
            Assert.True(task1.IsCompleted, "Task.IsCompleted-1");
            Assert.False(task1.IsFaulted, "Task.IsFaulted-1");

            pause2.Set();
            ok = task2.Wait(TimeSpan.FromMilliseconds(100));
            if (!ok) throw new TimeoutException();
            Assert.True(task2.IsCompleted, "Task.IsCompleted-2");
            Assert.False(task2.IsFaulted, "Task.IsFaulted-2");
        }

        [Fact, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_Task_StartTask_2()
        {
            UnitTestSchedulingContext cntx = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(cntx);

            ManualResetEvent pause1 = new ManualResetEvent(false);
            ManualResetEvent pause2 = new ManualResetEvent(false);
            Task task1 = new Task(() => { pause1.WaitOne(); output.WriteLine("Task-1"); });
            Task task2 = new Task(() => { pause2.WaitOne(); output.WriteLine("Task-2"); });

            pause1.Set();
            task1.Start(scheduler);

            bool ok = task1.Wait(TimeSpan.FromMilliseconds(100));
            if (!ok) throw new TimeoutException();

            Assert.True(task1.IsCompleted, "Task.IsCompleted-1");
            Assert.False(task1.IsFaulted, "Task.IsFaulted-1");

            task2.Start(scheduler);
            pause2.Set();
            ok = task2.Wait(TimeSpan.FromMilliseconds(100));
            if (!ok) throw new TimeoutException();

            Assert.True(task2.IsCompleted, "Task.IsCompleted-2");
            Assert.False(task2.IsFaulted, "Task.IsFaulted-2");
        }

        [Fact, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_Task_StartTask_Wrapped()
        {
            UnitTestSchedulingContext cntx = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(cntx);

            ManualResetEvent pause1 = new ManualResetEvent(false);
            ManualResetEvent pause2 = new ManualResetEvent(false);
            Task task1 = new Task(() => { pause1.WaitOne(); output.WriteLine("Task-1"); });
            Task task2 = new Task(() => { pause2.WaitOne(); output.WriteLine("Task-2"); });

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

            Assert.True(task1.IsCompleted, "Task.IsCompleted-1");
            Assert.False(task1.IsFaulted, "Task.IsFaulted-1");

            wrapper2.Start(scheduler);
            pause2.Set();
            bool finished = wrapper2.Wait(TimeSpan.FromMilliseconds(100));
            if (!finished) throw new TimeoutException();

            Assert.True(task2.IsCompleted, "Task.IsCompleted-2");
            Assert.False(task2.IsFaulted, "Task.IsFaulted-2");
        }

        [Fact, TestCategory("Functional"), TestCategory("TaskScheduler")]
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
                tasks[i] = new Task(() => { output.WriteLine("Inside Task-" + taskNum); flags[taskNum].WaitOne(); });
                output.WriteLine("Created Task-" + taskNum + " Id=" + tasks[taskNum].Id);
            }

            Task[] wrappers = new Task[NumTasks];
            for (int i = 0; i < NumTasks; i++)
            {
                int taskNum = i; // Capture
                wrappers[i] = new Task(() =>
                {
                    output.WriteLine("Inside Wrapper-" + taskNum); 
                    tasks[taskNum].Start(scheduler);
                });
                wrappers[i].ContinueWith(t =>
                {
                    Assert.False(t.IsFaulted, "Warpper.IsFaulted-" + taskNum + " " + t.Exception);
                    Assert.True(t.IsCompleted, "Wrapper.IsCompleted-" + taskNum);
                });
                output.WriteLine("Created Wrapper-" + taskNum + " Task.Id=" + wrappers[taskNum].Id);
            }

            foreach (var wrapper in wrappers) wrapper.Start(scheduler);
            foreach (var flag in flags) flag.Set();
            for (int i = 0; i < wrappers.Length; i++)
            {
                bool ok = wrappers[i].Wait(TimeSpan.FromMilliseconds(NumTasks * 150 * 2));
                Assert.True(ok, "Wait completed successfully for Wrapper-" + i);
            }

            for (int i = 0; i < tasks.Length; i++)
            {
                bool ok = tasks[i].Wait(TimeSpan.FromMilliseconds(NumTasks * 150 * 2));
                Assert.True(ok, "Wait completed successfully for Task-" + i);
                Assert.False(tasks[i].IsFaulted, "Task.IsFaulted-" + i + " " + tasks[i].Exception);
                Assert.True(tasks[i].IsCompleted, "Task.IsCompleted-" + i);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("TaskScheduler")]
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
                tasks[i] = new Task(() => { output.WriteLine("Inside Task-" + taskNum); flags[taskNum].WaitOne(); });
            }

            ClosureWorkItem[] workItems = new ClosureWorkItem[NumTasks];
            for (int i = 0; i < NumTasks; i++)
            {
                int taskNum = i; // Capture
                workItems[i] = new ClosureWorkItem(() =>
                {
                    output.WriteLine("Inside ClosureWorkItem-" + taskNum);
                    tasks[taskNum].Start(scheduler);
                    bool ok = tasks[taskNum].Wait(TimeSpan.FromMilliseconds(NumTasks * 100));
                    Assert.True(ok, "Wait completed successfully inside ClosureWorkItem-" + taskNum);
                });
            }

            foreach (var workItem in workItems) scheduler.QueueWorkItem(workItem, cntx);
            foreach (var flag in flags) flag.Set();
            for (int i = 0; i < tasks.Length; i++)
            {
                bool ok = tasks[i].Wait(TimeSpan.FromMilliseconds(NumTasks * 150));
                Assert.True(ok, "Wait completed successfully for Task-" + i);
            }


            for (int i = 0; i < tasks.Length; i++)
            {
                Assert.False(tasks[i].IsFaulted, "Task.IsFaulted-" + i + " Exception=" + tasks[i].Exception);
                Assert.True(tasks[i].IsCompleted, "Task.IsCompleted-" + i);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
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
                    output.WriteLine("#0 - TaskWorkItem - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.Equal(activationScheduler, TaskScheduler.Current); //

                    t1 = new Task(() =>
                    {
                        output.WriteLine("#1 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                            SynchronizationContext.Current, TaskScheduler.Current);
                        Assert.Equal(activationScheduler, TaskScheduler.Current);  // "TaskScheduler.Current #1"
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
            Assert.True(result0.Task.Exception == null, "Task-0 should not throw exception: " + result0.Task.Exception);
            Assert.True(result0.Task.Result, "Task-0 completed");

            Assert.NotNull(t1); // Task-1 started
            result1.Task.Wait(TimeSpan.FromSeconds(1));
            Assert.True(t1.IsCompleted, "Task-1 completed");
            Assert.False(t1.IsFaulted, "Task-1 faulted: " + t1.Exception);
            Assert.True(result1.Task.Result, "Task-1 completed");
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
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
                    output.WriteLine("#0 - TaskWorkItem - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.Equal(activationScheduler, TaskScheduler.Current);  // "TaskScheduler.Current #0"

                    t1 = new Task(() =>
                    {
                        output.WriteLine("#1 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                            SynchronizationContext.Current, TaskScheduler.Current);
                        Assert.Equal(scheduler, TaskScheduler.Current);  // "TaskScheduler.Current #1"
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
            Assert.True(result0.Task.Exception == null, "Task-0 should not throw exception: " + result0.Task.Exception);
            Assert.True(result0.Task.Result, "Task-0 completed");

            Assert.NotNull(t1); // Task-1 started
            result1.Task.Wait(TimeSpan.FromSeconds(1));
            Assert.True(t1.IsCompleted, "Task-1 completed");
            Assert.False(t1.IsFaulted, "Task-1 faulted: " + t1.Exception);
            Assert.True(result1.Task.Result, "Task-1 completed");
        }

        [Fact, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_Task_NewTask_ContinueWith_Wrapped_OrleansTaskScheduler()
        {
            UnitTestSchedulingContext rootContext = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(rootContext);

            Task wrapped = new Task(() =>
            {
                output.WriteLine("#0 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                    SynchronizationContext.Current, TaskScheduler.Current);

                Task t0 = new Task(() =>
                {
                    output.WriteLine("#1 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.Equal(scheduler, TaskScheduler.Current);  // "TaskScheduler.Current #1"
                });
                Task t1 = t0.ContinueWith(task =>
                {
                    Assert.False(task.IsFaulted, "Task #1 Faulted=" + task.Exception);

                    output.WriteLine("#2 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.Equal(scheduler, TaskScheduler.Current);  // "TaskScheduler.Current #2"
                });
                t0.Start(scheduler);
                bool ok = t1.Wait(TimeSpan.FromSeconds(15));
                if (!ok) throw new TimeoutException();
            });
            wrapped.Start(scheduler);
            bool finished = wrapped.Wait(TimeSpan.FromSeconds(30));
            if (!finished) throw new TimeoutException();
        }

        [Fact, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_Task_NewTask_ContinueWith_TaskScheduler()
        {
            UnitTestSchedulingContext rootContext = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(rootContext);

            output.WriteLine("#0 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                SynchronizationContext.Current, TaskScheduler.Current);

            Task t0 = new Task(() =>
            {
                output.WriteLine("#1 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}", 
                    SynchronizationContext.Current, TaskScheduler.Current);
                Assert.Equal(scheduler, TaskScheduler.Current);  // "TaskScheduler.Current #1"
            });
            Task t1 = t0.ContinueWith(task =>
            {
                Assert.False(task.IsFaulted, "Task #1 Faulted=" + task.Exception);

                output.WriteLine("#2 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                    SynchronizationContext.Current, TaskScheduler.Current);
                Assert.Equal(scheduler, TaskScheduler.Current);  // "TaskScheduler.Current #2"
            }, scheduler);
            t0.Start(scheduler);
            bool ok = t1.Wait(TimeSpan.FromSeconds(30));
            if (!ok) throw new TimeoutException();
        }

        [Fact, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_Task_StartNew_ContinueWith_TaskScheduler()
        {
            UnitTestSchedulingContext rootContext = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(rootContext);

            output.WriteLine("#0 - StartNew - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                SynchronizationContext.Current, TaskScheduler.Current);

            Task t0 = Task.Factory.StartNew(state =>
            {
                output.WriteLine("#1 - StartNew - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                    SynchronizationContext.Current, TaskScheduler.Current);
                Assert.Equal(scheduler, TaskScheduler.Current);  // "TaskScheduler.Current #1"
            }, null, CancellationToken.None, TaskCreationOptions.None, scheduler);
            Task t1 = t0.ContinueWith(task =>
            {
                Assert.False(task.IsFaulted, "Task #1 Faulted=" + task.Exception);

                output.WriteLine("#2 - StartNew - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                    SynchronizationContext.Current, TaskScheduler.Current);
                Assert.Equal(scheduler, TaskScheduler.Current);  // "TaskScheduler.Current #2"
            }, scheduler);
            bool ok = t1.Wait(TimeSpan.FromSeconds(30));
            if (!ok) throw new TimeoutException();
        }

        [Fact, TestCategory("Functional"), TestCategory("TaskScheduler")]
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
                        output.WriteLine("Sub-task " + id + " sleeping");
                        Thread.Sleep(100);
                        output.WriteLine("Sub-task " + id + " awake");
                        n = k + 1;
                        // ReSharper restore AccessToModifiedClosure
                    };
                    Task.Factory.StartNew(action).ContinueWith(tsk =>
                    {
                        LogContext("Sub-task " + id + "-ContinueWith");

                        output.WriteLine("Sub-task " + id + " Done");
                    });
                }
            };

            IWorkItem workItem = new ClosureWorkItem(closure);

            scheduler.QueueWorkItem(workItem, context);

            // Pause to let things run
            output.WriteLine("Main-task sleeping");
            Thread.Sleep(TimeSpan.FromSeconds(2));
            output.WriteLine("Main-task awake");

            // N should be 10, because all tasks should execute serially
            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(10, n);  // "Work items executed concurrently"
            scheduler.Stop();
        }

        [Fact, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_Task_RequestContext_NewTask_ContinueWith()
        {
            UnitTestSchedulingContext rootContext = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(rootContext);

            const string key = "K";
            int val = TestConstants.random.Next();
            RequestContext.Set(key, val);

            output.WriteLine("Initial - SynchronizationContext.Current={0} TaskScheduler.Current={1} Thread={2}",
                SynchronizationContext.Current, TaskScheduler.Current, Thread.CurrentThread.ManagedThreadId);

            Assert.Equal(val, RequestContext.Get(key));  // "RequestContext.Get Initial"

            Task t0 = new Task(() =>
            {
                output.WriteLine("#0 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1} Thread={2}",
                    SynchronizationContext.Current, TaskScheduler.Current, Thread.CurrentThread.ManagedThreadId);

                Assert.Equal(val, RequestContext.Get(key));  // "RequestContext.Get #0"

                Task t1 = new Task(() =>
                {
                    output.WriteLine("#1 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1} Thread={2}",
                        SynchronizationContext.Current, TaskScheduler.Current, Thread.CurrentThread.ManagedThreadId);
                    Assert.Equal(val, RequestContext.Get(key));  // "RequestContext.Get #1"
                });
                Task t2 = t1.ContinueWith(task =>
                {
                    Assert.False(task.IsFaulted, "Task #1 FAULTED=" + task.Exception);

                    output.WriteLine("#2 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1} Thread={2}",
                        SynchronizationContext.Current, TaskScheduler.Current, Thread.CurrentThread.ManagedThreadId);
                    Assert.Equal(val, RequestContext.Get(key));  // "RequestContext.Get #2"
                });
                t1.Start(scheduler);
                bool ok = t2.Wait(TimeSpan.FromSeconds(5));
                if (!ok) throw new TimeoutException();
            });
            t0.Start(scheduler);
            bool finished = t0.Wait(TimeSpan.FromSeconds(10));
            if (!finished) throw new TimeoutException();
            Assert.False(t0.IsFaulted, "Task #0 FAULTED=" + t0.Exception);
        }

        [Fact, TestCategory("Functional"), TestCategory("TaskScheduler")]
        public void Sched_AC_RequestContext_StartNew_ContinueWith()
        {
            UnitTestSchedulingContext rootContext = new UnitTestSchedulingContext();
            OrleansTaskScheduler scheduler = TestInternalHelper.InitializeSchedulerForTesting(rootContext);

            const string key = "A";
            int val = TestConstants.random.Next();
            RequestContext.Set(key, val);

            output.WriteLine("Initial - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                SynchronizationContext.Current, TaskScheduler.Current);

            Assert.Equal(val, RequestContext.Get(key));  // "RequestContext.Get Initial"

            Task t0 = Task.Factory.StartNew(() =>
            {
                output.WriteLine("#0 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                    SynchronizationContext.Current, TaskScheduler.Current);

                Assert.Equal(val, RequestContext.Get(key));  // "RequestContext.Get #0"

                Task t1 = Task.Factory.StartNew(() =>
                {
                    output.WriteLine("#1 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.Equal(val, RequestContext.Get(key));  // "RequestContext.Get #1"
                });
                Task t2 = t1.ContinueWith((_) =>
                {
                    output.WriteLine("#2 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.Equal(val, RequestContext.Get(key));  // "RequestContext.Get #2"
                });
                t2.Wait(TimeSpan.FromSeconds(5));
            });
            t0.Wait(TimeSpan.FromSeconds(10));
            Assert.True(t0.IsCompleted, "Task #0 FAULTED=" + t0.Exception);
        }

        private void LogContext(string what)
        {
            lock (lockable)
            {
                output.WriteLine(
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
                //output.WriteLine("Backtrace: " + st);
            }
        }

        internal static void InitSchedulerLogging()
        {
            LogManager.UnInitialize();
            //LogManager.LogConsumers.Add(new LogWriterToConsole());
            if (!LogManager.TelemetryConsumers.OfType<ConsoleTelemetryConsumer>().Any())
            {
                LogManager.TelemetryConsumers.Add(new ConsoleTelemetryConsumer());
            }

            var traceLevels = new[]
            {
                Tuple.Create("Scheduler", Severity.Verbose3),
                Tuple.Create("Scheduler.WorkerPoolThread", Severity.Verbose2),
            };
            LogManager.SetTraceLevelOverrides(new List<Tuple<string, Severity>>(traceLevels));

            var orleansConfig = new ClusterConfiguration();
            orleansConfig.StandardLoad();
            NodeConfiguration config = orleansConfig.CreateNodeConfigurationForSilo("Primary");
            StatisticsCollector.Initialize(config);
            SchedulerStatisticsGroup.Init();
        }
    }
}

// ReSharper restore ConvertToConstant.Local
