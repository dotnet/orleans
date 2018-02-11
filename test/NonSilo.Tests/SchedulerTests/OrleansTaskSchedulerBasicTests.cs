using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Scheduler;
using TestExtensions;
using UnitTests.TesterInternal;
using Xunit;
using Xunit.Abstractions;
using Orleans;
using Orleans.TestingHost.Utils;
using Orleans.Statistics;
using Orleans.Hosting;
using Microsoft.Extensions.Options;

using Orleans.Runtime.TestHooks;

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
    
    [TestCategory("BVT"), TestCategory("Scheduler")]
    public class OrleansTaskSchedulerBasicTests : IDisposable
    {
        private readonly ITestOutputHelper output;
        private static readonly object Lockable = new object();
        private readonly IHostEnvironmentStatistics performanceMetrics;
        private readonly UnitTestSchedulingContext rootContext;
        private readonly OrleansTaskScheduler scheduler;
        private readonly ILoggerFactory loggerFactory;
        public OrleansTaskSchedulerBasicTests(ITestOutputHelper output)
        {
            this.output = output;
            SynchronizationContext.SetSynchronizationContext(null);
            this.loggerFactory = InitSchedulerLogging();
            this.performanceMetrics = new TestHooksHostEnvironmentStatistics();
            this.rootContext = new UnitTestSchedulingContext();
            this.scheduler = TestInternalHelper.InitializeSchedulerForTesting(this.rootContext, this.performanceMetrics, this.loggerFactory);
        }
        
        public void Dispose()
        {
            SynchronizationContext.SetSynchronizationContext(null);
            this.scheduler.Stop();
        }

        [Fact, TestCategory("AsynchronyPrimitives")]
        public void Async_Task_Start_OrleansTaskScheduler()
        {
            int expected = 2;
            bool done = false;
            Task<int> t = new Task<int>(() => { done = true; return expected; });
            t.Start(this.scheduler);

            int received = t.Result;
            Assert.True(t.IsCompleted, "Task should have completed");
            Assert.False(t.IsFaulted, "Task should not thrown exception: " + t.Exception);
            Assert.True(done, "Task should be done");
            Assert.Equal(expected, received);      
        }

        [Fact, TestCategory("AsynchronyPrimitives")]
        public void Async_Task_Start_ActivationTaskScheduler()
        {
            ActivationTaskScheduler activationScheduler = this.scheduler.GetWorkItemGroup(this.rootContext).TaskRunner;

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

        [Fact]
        public void Sched_SimpleFifoTest()
        {
            // This is not a great test because there's a 50/50 shot that it will work even if the scheduling
            // is completely and thoroughly broken and both closures are executed "simultaneously"
            ActivationTaskScheduler activationScheduler = this.scheduler.GetWorkItemGroup(this.rootContext).TaskRunner;

            int n = 0;
            // ReSharper disable AccessToModifiedClosure
            IWorkItem item1 = new ClosureWorkItem(() => { n = n + 5; });
            IWorkItem item2 = new ClosureWorkItem(() => { n = n * 3; });
            // ReSharper restore AccessToModifiedClosure
            this.scheduler.QueueWorkItem(item1, this.rootContext);
            this.scheduler.QueueWorkItem(item2, this.rootContext);

            // Pause to let things run
            Thread.Sleep(1000);

            // N should be 15, because the two tasks should execute in order
            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(15, n);
            this.output.WriteLine("Test executed OK.");
        }

        [Fact]
        public async Task Sched_Task_TplFifoTest()
        {
            // This is not a great test because there's a 50/50 shot that it will work even if the scheduling
            // is completely and thoroughly broken and both closures are executed "simultaneously"
            ActivationTaskScheduler activationScheduler = this.scheduler.GetWorkItemGroup(this.rootContext).TaskRunner;

            int n = 0;

            // ReSharper disable AccessToModifiedClosure
            Task task1 = new Task(() => { Thread.Sleep(1000); n = n + 5; });
            Task task2 = new Task(() => { n = n * 3; });
            // ReSharper restore AccessToModifiedClosure

            task1.Start(activationScheduler);
            task2.Start(activationScheduler);

            await Task.WhenAll(task1, task2).WithTimeout(TimeSpan.FromSeconds(5));

            // N should be 15, because the two tasks should execute in order
            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(15, n);
        }

        [Fact]
        public async Task Sched_Task_StartTask_1()
        {
            ManualResetEvent pause1 = new ManualResetEvent(false);
            ManualResetEvent pause2 = new ManualResetEvent(false);
            Task task1 = new Task(() => { pause1.WaitOne(); this.output.WriteLine("Task-1"); });
            Task task2 = new Task(() => { pause2.WaitOne(); this.output.WriteLine("Task-2"); });

            task1.Start(this.scheduler);
            task2.Start(this.scheduler);

            pause1.Set();
            await task1.WithTimeout(TimeSpan.FromMilliseconds(100));
            Assert.True(task1.IsCompleted, "Task.IsCompleted-1");
            Assert.False(task1.IsFaulted, "Task.IsFaulted-1");

            pause2.Set();
            await task2.WithTimeout(TimeSpan.FromMilliseconds(100));
            Assert.True(task2.IsCompleted, "Task.IsCompleted-2");
            Assert.False(task2.IsFaulted, "Task.IsFaulted-2");
        }

        [Fact]
        public async Task Sched_Task_StartTask_2()
        {
            ManualResetEvent pause1 = new ManualResetEvent(false);
            ManualResetEvent pause2 = new ManualResetEvent(false);
            Task task1 = new Task(() => { pause1.WaitOne(); this.output.WriteLine("Task-1"); });
            Task task2 = new Task(() => { pause2.WaitOne(); this.output.WriteLine("Task-2"); });

            pause1.Set();
            task1.Start(this.scheduler);

            await task1.WithTimeout(TimeSpan.FromMilliseconds(100));

            Assert.True(task1.IsCompleted, "Task.IsCompleted-1");
            Assert.False(task1.IsFaulted, "Task.IsFaulted-1");

            task2.Start(this.scheduler);
            pause2.Set();
            await task2.WithTimeout(TimeSpan.FromMilliseconds(100));

            Assert.True(task2.IsCompleted, "Task.IsCompleted-2");
            Assert.False(task2.IsFaulted, "Task.IsFaulted-2");
        }

        [Fact]
        public async Task Sched_Task_StartTask_Wrapped()
        {
            ManualResetEvent pause1 = new ManualResetEvent(false);
            ManualResetEvent pause2 = new ManualResetEvent(false);
            Task task1 = new Task(() => { pause1.WaitOne(); this.output.WriteLine("Task-1"); });
            Task task2 = new Task(() => { pause2.WaitOne(); this.output.WriteLine("Task-2"); });

            Task wrapper1 = new Task(() =>
            {
                task1.Start(this.scheduler);
                task1.WaitWithThrow(TimeSpan.FromSeconds(10));
            });
            Task wrapper2 = new Task(() =>
            {
                task2.Start(this.scheduler);
                task2.WaitWithThrow(TimeSpan.FromSeconds(10));
            });

            pause1.Set();
            wrapper1.Start(this.scheduler);
            await wrapper1.WithTimeout(TimeSpan.FromSeconds(10));

            Assert.True(task1.IsCompleted, "Task.IsCompleted-1");
            Assert.False(task1.IsFaulted, "Task.IsFaulted-1");

            wrapper2.Start(this.scheduler);
            pause2.Set();
            await wrapper2.WithTimeout(TimeSpan.FromSeconds(10));

            Assert.True(task2.IsCompleted, "Task.IsCompleted-2");
            Assert.False(task2.IsFaulted, "Task.IsFaulted-2");
        }

        [Fact]
        public void Sched_Task_StartTask_Wait_Wrapped()
        {
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
                tasks[i] = new Task(() => { this.output.WriteLine("Inside Task-" + taskNum); flags[taskNum].WaitOne(); });
                this.output.WriteLine("Created Task-" + taskNum + " Id=" + tasks[taskNum].Id);
            }

            Task[] wrappers = new Task[NumTasks];
            for (int i = 0; i < NumTasks; i++)
            {
                int taskNum = i; // Capture
                wrappers[i] = new Task(() =>
                {
                    this.output.WriteLine("Inside Wrapper-" + taskNum); 
                    tasks[taskNum].Start(this.scheduler);
                });
                wrappers[i].ContinueWith(t =>
                {
                    Assert.False(t.IsFaulted, "Warpper.IsFaulted-" + taskNum + " " + t.Exception);
                    Assert.True(t.IsCompleted, "Wrapper.IsCompleted-" + taskNum);
                });
                this.output.WriteLine("Created Wrapper-" + taskNum + " Task.Id=" + wrappers[taskNum].Id);
            }

            foreach (var wrapper in wrappers) wrapper.Start(this.scheduler);
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

        [Fact]
        public void Sched_Task_ClosureWorkItem_Wait()
        {
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
                tasks[i] = new Task(() => { this.output.WriteLine("Inside Task-" + taskNum); flags[taskNum].WaitOne(); });
            }

            ClosureWorkItem[] workItems = new ClosureWorkItem[NumTasks];
            for (int i = 0; i < NumTasks; i++)
            {
                int taskNum = i; // Capture
                workItems[i] = new ClosureWorkItem(() =>
                {
                    this.output.WriteLine("Inside ClosureWorkItem-" + taskNum);
                    tasks[taskNum].Start(this.scheduler);
                    bool ok = tasks[taskNum].Wait(TimeSpan.FromMilliseconds(NumTasks * 100));
                    Assert.True(ok, "Wait completed successfully inside ClosureWorkItem-" + taskNum);
                });
            }

            foreach (var workItem in workItems) this.scheduler.QueueWorkItem(workItem, this.rootContext);
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

        [Fact]
        public async Task Sched_Task_TaskWorkItem_CurrentScheduler()
        {
            ActivationTaskScheduler activationScheduler = this.scheduler.GetWorkItemGroup(this.rootContext).TaskRunner;

            var result0 = new TaskCompletionSource<bool>();
            var result1 = new TaskCompletionSource<bool>();

            Task t1 = null;
            this.scheduler.QueueWorkItem(new ClosureWorkItem(() =>
            {
                try
                {
                    this.output.WriteLine("#0 - TaskWorkItem - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.Equal(activationScheduler, TaskScheduler.Current); //

                    t1 = new Task(() =>
                    {
                        this.output.WriteLine("#1 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
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
            }), this.rootContext);

            await result0.Task.WithTimeout(TimeSpan.FromMinutes(1));
            Assert.True(result0.Task.Exception == null, "Task-0 should not throw exception: " + result0.Task.Exception);
            Assert.True(result0.Task.Result, "Task-0 completed");

            Assert.NotNull(t1); // Task-1 started
            await result1.Task.WithTimeout(TimeSpan.FromMinutes(1));
            // give a minimum extra chance to yield after result0 has been set, as it might not have finished the t1 task
            await t1.WithTimeout(TimeSpan.FromMilliseconds(1));

            Assert.True(t1.IsCompleted, "Task-1 completed");
            Assert.False(t1.IsFaulted, "Task-1 faulted: " + t1.Exception);
            Assert.True(result1.Task.Result, "Task-1 completed");
        }

        [Fact]
        public async Task Sched_Task_ClosureWorkItem_SpecificScheduler()
        {
            ActivationTaskScheduler activationScheduler = this.scheduler.GetWorkItemGroup(this.rootContext).TaskRunner;

            var result0 = new TaskCompletionSource<bool>();
            var result1 = new TaskCompletionSource<bool>();

            Task t1 = null;
            this.scheduler.QueueWorkItem(new ClosureWorkItem(() =>
            {
                try
                {
                    this.output.WriteLine("#0 - TaskWorkItem - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.Equal(activationScheduler, TaskScheduler.Current);  // "TaskScheduler.Current #0"

                    t1 = new Task(() =>
                    {
                        this.output.WriteLine("#1 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                            SynchronizationContext.Current, TaskScheduler.Current);
                        Assert.Equal(this.scheduler, TaskScheduler.Current);  // "TaskScheduler.Current #1"
                        result1.SetResult(true);
                    });
                    t1.Start(this.scheduler);

                    result0.SetResult(true);
                }
                catch (Exception exc)
                {
                    result0.SetException(exc);
                }
            }), this.rootContext);

            await result0.Task.WithTimeout(TimeSpan.FromMinutes(1));
            Assert.True(result0.Task.Exception == null, "Task-0 should not throw exception: " + result0.Task.Exception);
            Assert.True(result0.Task.Result, "Task-0 completed");

            Assert.NotNull(t1); // Task-1 started
            await result1.Task.WithTimeout(TimeSpan.FromMinutes(1));
            // give a minimum extra chance to yield after result0 has been set, as it might not have finished the t1 task
            await t1.WithTimeout(TimeSpan.FromMilliseconds(1));

            Assert.True(t1.IsCompleted, "Task-1 completed");
            Assert.False(t1.IsFaulted, "Task-1 faulted: " + t1.Exception);
            Assert.True(result1.Task.Result, "Task-1 completed");
        }

        [Fact]
        public void Sched_Task_NewTask_ContinueWith_Wrapped_OrleansTaskScheduler()
        {
            Task wrapped = new Task(() =>
            {
                this.output.WriteLine("#0 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                    SynchronizationContext.Current, TaskScheduler.Current);

                Task t0 = new Task(() =>
                {
                    this.output.WriteLine("#1 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.Equal(this.scheduler, TaskScheduler.Current);  // "TaskScheduler.Current #1"
                });
                Task t1 = t0.ContinueWith(task =>
                {
                    Assert.False(task.IsFaulted, "Task #1 Faulted=" + task.Exception);

                    this.output.WriteLine("#2 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.Equal(this.scheduler, TaskScheduler.Current);  // "TaskScheduler.Current #2"
                });
                t0.Start(this.scheduler);
                bool ok = t1.Wait(TimeSpan.FromSeconds(15));
                if (!ok) throw new TimeoutException();
            });
            wrapped.Start(this.scheduler);
            bool finished = wrapped.Wait(TimeSpan.FromSeconds(30));
            if (!finished) throw new TimeoutException();
        }

        [Fact]
        public void Sched_Task_NewTask_ContinueWith_TaskScheduler()
        {
            this.output.WriteLine("#0 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                SynchronizationContext.Current, TaskScheduler.Current);

            Task t0 = new Task(() =>
            {
                this.output.WriteLine("#1 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}", 
                    SynchronizationContext.Current, TaskScheduler.Current);
                Assert.Equal(this.scheduler, TaskScheduler.Current);  // "TaskScheduler.Current #1"
            });
            Task t1 = t0.ContinueWith(task =>
            {
                Assert.False(task.IsFaulted, "Task #1 Faulted=" + task.Exception);

                this.output.WriteLine("#2 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                    SynchronizationContext.Current, TaskScheduler.Current);
                Assert.Equal(this.scheduler, TaskScheduler.Current);  // "TaskScheduler.Current #2"
            }, this.scheduler);
            t0.Start(this.scheduler);
            t1.WaitWithThrow(TimeSpan.FromSeconds(30));
        }

        [Fact]
        public void Sched_Task_StartNew_ContinueWith_TaskScheduler()
        {
            this.output.WriteLine("#0 - StartNew - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                SynchronizationContext.Current, TaskScheduler.Current);

            Task t0 = Task.Factory.StartNew(state =>
            {
                this.output.WriteLine("#1 - StartNew - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                    SynchronizationContext.Current, TaskScheduler.Current);
                Assert.Equal(this.scheduler, TaskScheduler.Current);  // "TaskScheduler.Current #1"
            }, null, CancellationToken.None, TaskCreationOptions.None, this.scheduler);
            Task t1 = t0.ContinueWith(task =>
            {
                Assert.False(task.IsFaulted, "Task #1 Faulted=" + task.Exception);

                this.output.WriteLine("#2 - StartNew - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                    SynchronizationContext.Current, TaskScheduler.Current);
                Assert.Equal(this.scheduler, TaskScheduler.Current);  // "TaskScheduler.Current #2"
            }, this.scheduler);
            t1.WaitWithThrow(TimeSpan.FromSeconds(30));
        }

        [Fact]
        public void Sched_Task_SubTaskExecutionSequencing()
        {
            UnitTestSchedulingContext context = new UnitTestSchedulingContext();
            this.scheduler.RegisterWorkContext(context);

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
                        this.output.WriteLine("Sub-task " + id + " sleeping");
                        Thread.Sleep(100);
                        this.output.WriteLine("Sub-task " + id + " awake");
                        n = k + 1;
                        // ReSharper restore AccessToModifiedClosure
                    };
                    Task.Factory.StartNew(action).ContinueWith(tsk =>
                    {
                        LogContext("Sub-task " + id + "-ContinueWith");

                        this.output.WriteLine("Sub-task " + id + " Done");
                    });
                }
            };

            IWorkItem workItem = new ClosureWorkItem(closure);

            this.scheduler.QueueWorkItem(workItem, context);

            // Pause to let things run
            this.output.WriteLine("Main-task sleeping");
            Thread.Sleep(TimeSpan.FromSeconds(2));
            this.output.WriteLine("Main-task awake");

            // N should be 10, because all tasks should execute serially
            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(10, n);  // "Work items executed concurrently"
            this.scheduler.Stop();
        }
        // blocked on upgrate RequestContext to corelre compaible
        [Fact]
        public void Sched_Task_RequestContext_NewTask_ContinueWith()
        {
            const string key = "K";
            int val = TestConstants.random.Next();
            RequestContext.Set(key, val);

            this.output.WriteLine("Initial - SynchronizationContext.Current={0} TaskScheduler.Current={1} Thread={2}",
                SynchronizationContext.Current, TaskScheduler.Current, Thread.CurrentThread.ManagedThreadId);

            Assert.Equal(val, RequestContext.Get(key));  // "RequestContext.Get Initial"

            Task t0 = new Task(() =>
            {
                this.output.WriteLine("#0 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1} Thread={2}",
                    SynchronizationContext.Current, TaskScheduler.Current, Thread.CurrentThread.ManagedThreadId);

                Assert.Equal(val, RequestContext.Get(key));  // "RequestContext.Get #0"

                Task t1 = new Task(() =>
                {
                    this.output.WriteLine("#1 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1} Thread={2}",
                        SynchronizationContext.Current, TaskScheduler.Current, Thread.CurrentThread.ManagedThreadId);
                    Assert.Equal(val, RequestContext.Get(key));  // "RequestContext.Get #1"
                });
                Task t2 = t1.ContinueWith(task =>
                {
                    Assert.False(task.IsFaulted, "Task #1 FAULTED=" + task.Exception);

                    this.output.WriteLine("#2 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1} Thread={2}",
                        SynchronizationContext.Current, TaskScheduler.Current, Thread.CurrentThread.ManagedThreadId);
                    Assert.Equal(val, RequestContext.Get(key));  // "RequestContext.Get #2"
                });
                t1.Start(this.scheduler);
                t2.WaitWithThrow(TimeSpan.FromSeconds(5));
            });
            t0.Start(this.scheduler);
            t0.WaitWithThrow(TimeSpan.FromSeconds(10));
            Assert.False(t0.IsFaulted, "Task #0 FAULTED=" + t0.Exception);
        }

        [Fact]
        public void Sched_AC_RequestContext_StartNew_ContinueWith()
        {
            const string key = "A";
            int val = TestConstants.random.Next();
            RequestContext.Set(key, val);

            this.output.WriteLine("Initial - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                SynchronizationContext.Current, TaskScheduler.Current);

            Assert.Equal(val, RequestContext.Get(key));  // "RequestContext.Get Initial"

            Task t0 = Task.Factory.StartNew(() =>
            {
                this.output.WriteLine("#0 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                    SynchronizationContext.Current, TaskScheduler.Current);

                Assert.Equal(val, RequestContext.Get(key));  // "RequestContext.Get #0"

                Task t1 = Task.Factory.StartNew(() =>
                {
                    this.output.WriteLine("#1 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.Equal(val, RequestContext.Get(key));  // "RequestContext.Get #1"
                });
                Task t2 = t1.ContinueWith((_) =>
                {
                    this.output.WriteLine("#2 - new Task - SynchronizationContext.Current={0} TaskScheduler.Current={1}",
                        SynchronizationContext.Current, TaskScheduler.Current);
                    Assert.Equal(val, RequestContext.Get(key));  // "RequestContext.Get #2"
                });
                t2.Wait(TimeSpan.FromSeconds(5));
            });
            t0.Wait(TimeSpan.FromSeconds(10));
            Assert.True(t0.IsCompleted, "Task #0 FAULTED=" + t0.Exception);
        }

        [Fact]
        public async Task RequestContextProtectedInQueuedTasksTest()
        {
            string key = Guid.NewGuid().ToString();
            string value = Guid.NewGuid().ToString();

            // Caller RequestContext is protected from clear within QueueTask
            RequestContext.Set(key, value);
            await this.scheduler.QueueTask(() => AsyncCheckClearRequestContext(key), this.rootContext);
            Assert.Equal(value, (string)RequestContext.Get(key));

            // Caller RequestContext is protected from clear within QueueTask even if work is not actually asynchronous.
            await this.scheduler.QueueTask(() => NonAsyncCheckClearRequestContext(key), this.rootContext);
            Assert.Equal(value, (string)RequestContext.Get(key));

            // Caller RequestContext is protected from clear when work is asynchronous.
            Func<Task> asyncCheckClearRequestContext = async () =>
            {
                RequestContext.Clear();
                Assert.Null(RequestContext.Get(key));
                await Task.Delay(TimeSpan.Zero);
            };
            await asyncCheckClearRequestContext();
            Assert.Equal(value, (string)RequestContext.Get(key));

            // Caller RequestContext is NOT protected from clear when work is not asynchronous.
            Func<Task> nonAsyncCheckClearRequestContext = () =>
            {
                RequestContext.Clear();
                Assert.Null(RequestContext.Get(key));
                return Task.CompletedTask;
            };
            await nonAsyncCheckClearRequestContext();
            Assert.Null(RequestContext.Get(key));
        }

        private async Task AsyncCheckClearRequestContext(string key)
        {
            Assert.Null(RequestContext.Get(key));
            await Task.Delay(TimeSpan.Zero);
        }

        private Task NonAsyncCheckClearRequestContext(string key)
        {
            Assert.Null(RequestContext.Get(key));
            return Task.CompletedTask;
        }

        private void LogContext(string what)
        {
            lock (Lockable)
            {
                this.output.WriteLine(
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

        internal static ILoggerFactory InitSchedulerLogging()
        {
            var filters = new LoggerFilterOptions();
            filters.AddFilter("Scheduler", LogLevel.Trace);
            filters.AddFilter("Scheduler.WorkerPoolThread", LogLevel.Trace);
            var orleansConfig = new ClusterConfiguration();
            orleansConfig.StandardLoad();
            NodeConfiguration config = orleansConfig.CreateNodeConfigurationForSilo("Primary");
            var loggerFactory = TestingUtils.CreateDefaultLoggerFactory(TestingUtils.CreateTraceFileName(config.SiloName, orleansConfig.Globals.ClusterId), filters);
            StatisticsCollector.Initialize(StatisticsLevel.Info);
            SchedulerStatisticsGroup.Init(loggerFactory);
            return loggerFactory;
        }
    }
}

// ReSharper restore ConvertToConstant.Local
