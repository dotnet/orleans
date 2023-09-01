using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using Orleans.Internal;
using UnitTests.TesterInternal;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.SchedulerTests
{
    public class OrleansTaskSchedulerAdvancedTests : IDisposable
    {
        private readonly ITestOutputHelper output;

        private bool mainDone;
        private int stageNum1;
        private int stageNum2;

        private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan TwoSeconds = TimeSpan.FromSeconds(2);

        private static readonly int WaitFactor = Debugger.IsAttached ? 100 : 1;
        private readonly ILoggerFactory loggerFactory;

        public OrleansTaskSchedulerAdvancedTests(ITestOutputHelper output)
        {
            this.output = output;
            this.loggerFactory = OrleansTaskSchedulerBasicTests.InitSchedulerLogging();
        }

        public void Dispose()
        {
            this.loggerFactory.Dispose();
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public void Sched_AC_Test()
        {
            int n = 0;
            bool insideTask = false;
            UnitTestSchedulingContext context = new UnitTestSchedulingContext();
            var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            context.Scheduler = workItemGroup;

            this.output.WriteLine("Running Main in Context=" + RuntimeContext.Current);
            context.Scheduler.QueueAction(() =>
                {
                    for (int i = 0; i < 10; i++)
                    {
                        Task.Factory.StartNew(() =>
                        {
                            // ReSharper disable AccessToModifiedClosure
                            this.output.WriteLine("Starting " + i + " in Context=" + RuntimeContext.Current);
                            Assert.False(insideTask, $"Starting new task when I am already inside task of iteration {n}");
                            insideTask = true;
                            int k = n;
                            Thread.Sleep(100);
                            n = k + 1;
                            insideTask = false;
                            // ReSharper restore AccessToModifiedClosure
                        }).Ignore();
                    }
                });

            // Pause to let things run
            Thread.Sleep(1500);

            // N should be 10, because all tasks should execute serially
            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(10,  n);  // "Work items executed concurrently"
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task Sched_AC_WaitTest()
        {
            int n = 0;
            bool insideTask = false;
            UnitTestSchedulingContext context = new UnitTestSchedulingContext();
            var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            context.Scheduler = workItemGroup;

            var result = new TaskCompletionSource<bool>();

            context.Scheduler.QueueAction(() =>
                {
                    var task1 = Task.Factory.StartNew(() => 
                    {
                        this.output.WriteLine("Starting 1"); 
                        Assert.False(insideTask, $"Starting new task when I am already inside task of iteration {n}");
                        insideTask = true;
                        this.output.WriteLine("===> 1a"); 
                        Thread.Sleep(1000); n = n + 3;
                        this.output.WriteLine("===> 1b");
                        insideTask = false;
                    });
                    var task2 = Task.Factory.StartNew(() =>
                    {
                        this.output.WriteLine("Starting 2");
                        Assert.False(insideTask, $"Starting new task when I am already inside task of iteration {n}");
                        insideTask = true;
                        this.output.WriteLine("===> 2a");
                        task1.Wait();
                        this.output.WriteLine("===> 2b");
                        n = n * 5;
                        this.output.WriteLine("===> 2c");
                        insideTask = false;
                        result.SetResult(true);
                    });
                    task1.Ignore();
                    task2.Ignore();
                });

            var timeoutLimit = TimeSpan.FromMilliseconds(1500);
            try
            {
                await result.Task.WithTimeout(timeoutLimit);
            }
            catch (TimeoutException)
            {
                Assert.True(false, "Result did not arrive before timeout " + timeoutLimit);
            }

            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(15,  n);  // "Work items executed out of order"
        }

        private void SubProcess1(int n)
        {
            string msg = string.Format("1-{0} MainDone={1} inside Task {2}", n, this.mainDone, Task.CurrentId);
            this.output.WriteLine("1 ===> " + msg);
            Assert.True(this.mainDone, msg + " -- Main turn should be finished");
            this.stageNum1 = n;
        }

        private void SubProcess2(int n)
        {
            string msg = string.Format("2-{0} MainDone={1} inside Task {2}", n, this.mainDone, Task.CurrentId);
            this.output.WriteLine("2 ===> " + msg);
            Assert.True(this.mainDone, msg + " -- Main turn should be finished");
            this.stageNum2 = n;
        }
    
        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task Sched_AC_Turn_Execution_Order()
        {
            // Can we add a unit test that basicaly checks that any turn is indeed run till completion before any other turn? 
            // For example, you have a  long running main turn and in the middle it spawns a lot of short CWs (on Done promise) and StartNew. 
            // You test that no CW/StartNew runs until the main turn is fully done. And run in stress.

            UnitTestSchedulingContext context = new UnitTestSchedulingContext();
            var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            context.Scheduler = workItemGroup;

            var result1 = new TaskCompletionSource<bool>();
            var result2 = new TaskCompletionSource<bool>();

            context.Scheduler.QueueAction(() =>
            {
                this.mainDone = false;
                this.stageNum1 = this.stageNum2 = 0;

                Task task1 = Task.Factory.StartNew(() => SubProcess1(11));
                Task task2 = task1.ContinueWith((_) => SubProcess1(12));
                Task task3 = task2.ContinueWith((_) => SubProcess1(13));
                Task task4 = task3.ContinueWith((_) => { SubProcess1(14); result1.SetResult(true); });
                task4.Ignore();

                Task task21 = Task.CompletedTask.ContinueWith((_) => SubProcess2(21));
                Task task22 = task21.ContinueWith((_) => { SubProcess2(22); result2.SetResult(true); });
                task22.Ignore();

                Thread.Sleep(TimeSpan.FromSeconds(1));
                this.mainDone = true;
            });

            try { await result1.Task.WithTimeout(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { Assert.True(false, "Timeout-1"); }
            try { await result2.Task.WithTimeout(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { Assert.True(false, "Timeout-2"); }

            Assert.NotEqual(0, this.stageNum1); // "Work items did not get executed-1"
            Assert.NotEqual(0, this.stageNum2);  // "Work items did not get executed-2"
            Assert.Equal(14, this.stageNum1);  // "Work items executed out of order-1"
            Assert.Equal(22, this.stageNum2);  // "Work items executed out of order-2"
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task Sched_Stopped_WorkItemGroup()
        {
            var context = new UnitTestSchedulingContext();
            var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            context.Scheduler = workItemGroup;

            void CheckScheduler(object state)
            {
                Assert.IsType<string>(state);
                Assert.Equal("some state", state as string);
                Assert.IsType<ActivationTaskScheduler>(TaskScheduler.Current);
            }

            Task<Task> ScheduleTask() => Task.Factory.StartNew(
                state =>
                {
                    CheckScheduler(state);

                    return Task.Factory.StartNew(
                        async s =>
                        {
                            CheckScheduler(s);
                            await Task.Delay(50);
                            CheckScheduler(s);
                        },
                        state).Unwrap();
                },
                "some state",
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                workItemGroup.TaskScheduler);

            // Check that the WorkItemGroup is functioning.
            await await ScheduleTask();

            var taskAfterStopped = ScheduleTask();
            var resultTask = await Task.WhenAny(taskAfterStopped, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(taskAfterStopped, resultTask);

            await await taskAfterStopped;

            // Wait for the WorkItemGroup to upgrade the warning to an error and try again.
            // This delay is based upon SchedulingOptions.StoppedActivationWarningInterval.
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            taskAfterStopped = ScheduleTask();
            resultTask = await Task.WhenAny(taskAfterStopped, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(taskAfterStopped, resultTask);

            await await taskAfterStopped;
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public void Sched_Task_Turn_Execution_Order()
        {
            // A unit test that checks that any turn is indeed run till completion before any other turn? 
            // For example, you have a long running main turn and in the middle it spawns a lot of short CWs (on Done promise) and StartNew. 
            // You test that no CW/StartNew runs until the main turn is fully done. And run in stress.

            UnitTestSchedulingContext context = new UnitTestSchedulingContext();
            WorkItemGroup workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, this.loggerFactory);
            context.Scheduler = workItemGroup;
            ActivationTaskScheduler activationScheduler = workItemGroup.TaskScheduler;

            this.mainDone = false;
            this.stageNum1 = this.stageNum2 = 0;

            var result1 = new TaskCompletionSource<bool>();
            var result2 = new TaskCompletionSource<bool>();

            Task wrapper = null;
            Task finalTask1 = null;
            Task finalPromise2 = null;
            context.Scheduler.QueueAction(() =>
            {
                Log(1, "Outer ClosureWorkItem " + Task.CurrentId + " starting");
                Assert.Equal(activationScheduler,  TaskScheduler.Current);  // "TaskScheduler.Current #0"

                Log(2, "Starting wrapper Task");
                wrapper = Task.Factory.StartNew(() =>
                {
                    Log(3, "Inside wrapper Task Id=" + Task.CurrentId);
                    Assert.Equal(activationScheduler,  TaskScheduler.Current);  // "TaskScheduler.Current #1"

                    // Execution chain #1
                    Log(4, "Wrapper Task Id=" + Task.CurrentId + " creating Task chain");
                    Task task1 = Task.Factory.StartNew(() =>
                    {
                        Log(5, "#11 Inside sub-Task Id=" + Task.CurrentId);
                        Assert.Equal(activationScheduler,  TaskScheduler.Current);  // "TaskScheduler.Current #11"
                        SubProcess1(11);
                    });
                    Task task2 = task1.ContinueWith((Task task) =>
                    {
                        Log(6, "#12 Inside continuation Task Id=" + Task.CurrentId);
                        Assert.Equal(activationScheduler,  TaskScheduler.Current);  // "TaskScheduler.Current #12"
                        if (task.IsFaulted) throw task.Exception.Flatten();
                        SubProcess1(12);
                    });
                    Task task3 = task2.ContinueWith(task =>
                    {
                        Log(7, "#13 Inside continuation Task Id=" + Task.CurrentId);
                        Assert.Equal(activationScheduler,  TaskScheduler.Current);  // "TaskScheduler.Current #13"
                        if (task.IsFaulted) throw task.Exception.Flatten();
                        SubProcess1(13);
                    });
                    finalTask1 = task3.ContinueWith(task =>
                    {
                        Log(8, "#14 Inside final continuation Task Id=" + Task.CurrentId);
                        Assert.Equal(activationScheduler,  TaskScheduler.Current);  // "TaskScheduler.Current #14"
                        if (task.IsFaulted) throw task.Exception.Flatten();
                        SubProcess1(14);
                        result1.SetResult(true);
                    });

                    // Execution chain #2
                    Log(9, "Wrapper Task " + Task.CurrentId + " creating AC chain");
                    Task promise2 = Task.Factory.StartNew(() =>
                    {
                        Log(10, "#21 Inside sub-Task Id=" + Task.CurrentId);
                        Assert.Equal(activationScheduler,  TaskScheduler.Current);  // "TaskScheduler.Current #21"
                        SubProcess2(21);
                    });
                    finalPromise2 = promise2.ContinueWith((_) =>
                    {
                        Log(11, "#22 Inside final continuation Task Id=" + Task.CurrentId);
                        Assert.Equal(activationScheduler,  TaskScheduler.Current);  // "TaskScheduler.Current #22"
                        SubProcess2(22);
                        result2.SetResult(true);
                    });
                    finalPromise2.Ignore();

                    Log(12, "Wrapper Task Id=" + Task.CurrentId + " sleeping #2");
                    Thread.Sleep(TimeSpan.FromSeconds(1));

                    Log(13, "Wrapper Task Id=" + Task.CurrentId + " finished");
                });

                Log(14, "Outer ClosureWorkItem Task Id=" + Task.CurrentId + " sleeping");
                Thread.Sleep(TimeSpan.FromSeconds(1));
                Log(15, "Outer ClosureWorkItem Task Id=" + Task.CurrentId + " awake");

                Log(16, "Finished Outer ClosureWorkItem Task Id=" + wrapper.Id);
                this.mainDone = true;
            });

            Log(17, "Waiting for ClosureWorkItem to spawn wrapper Task");
            for (int i = 0; i < 5 * WaitFactor; i++)
            {
                if (wrapper != null) break;
                Thread.Sleep(TimeSpan.FromSeconds(1).Multiply(WaitFactor));
            }
            Assert.NotNull(wrapper); // Wrapper Task was not created

            Log(18, "Waiting for wrapper Task Id=" + wrapper.Id + " to complete");
            bool finished = wrapper.Wait(TimeSpan.FromSeconds(4 * WaitFactor));
            Log(19, "Done waiting for wrapper Task Id=" + wrapper.Id + " Finished=" + finished);
            if (!finished) throw new TimeoutException();
            Assert.False(wrapper.IsFaulted, "Wrapper Task faulted: " + wrapper.Exception);
            Assert.True(wrapper.IsCompleted, "Wrapper Task should be completed");

            Log(20, "Waiting for TaskWorkItem to complete");
            for (int i = 0; i < 15 * WaitFactor; i++)
            {
                if (this.mainDone) break;
                Thread.Sleep(1000 * WaitFactor);
            }
            Log(21, "Done waiting for TaskWorkItem to complete MainDone=" + this.mainDone);
            Assert.True(this.mainDone, "Main Task should be completed");
            Assert.NotNull(finalTask1); // Task chain #1 not created
            Assert.NotNull(finalPromise2); // Task chain #2 not created

            Log(22, "Waiting for final task #1 to complete");
            bool ok = finalTask1.Wait(TimeSpan.FromSeconds(4 * WaitFactor));
            Log(23, "Done waiting for final task #1 complete Ok=" + ok);
            if (!ok) throw new TimeoutException();
            Assert.False(finalTask1.IsFaulted, "Final Task faulted: " + finalTask1.Exception);
            Assert.True(finalTask1.IsCompleted, "Final Task completed");
            Assert.True(result1.Task.Result, "Timeout-1");

            Log(24, "Waiting for final promise #2 to complete");
            finalPromise2.Wait(TimeSpan.FromSeconds(4 * WaitFactor));
            Log(25, "Done waiting for final promise #2");
            Assert.False(finalPromise2.IsFaulted, "Final Task faulted: " + finalPromise2.Exception);
            Assert.True(finalPromise2.IsCompleted, "Final Task completed");
            Assert.True(result2.Task.Result, "Timeout-2");

            Assert.NotEqual(0, this.stageNum1);  // "Work items did not get executed-1"
            Assert.Equal(14, this.stageNum1);  // "Work items executed out of order-1"
            Assert.NotEqual(0, this.stageNum2);  // "Work items did not get executed-2"
            Assert.Equal(22, this.stageNum2);  // "Work items executed out of order-2"
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public void Sched_AC_Current_TaskScheduler()
        {
            UnitTestSchedulingContext context = new UnitTestSchedulingContext();
            var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory); 
            context.Scheduler = workItemGroup;
            ActivationTaskScheduler activationScheduler = workItemGroup.TaskScheduler;

            this.mainDone = false;

            var result = new TaskCompletionSource<bool>();

            Task wrapper = null;
            Task finalPromise = null;
            context.Scheduler.QueueAction(() =>
            {
                Log(1, "Outer ClosureWorkItem " + Task.CurrentId + " starting");
                Assert.Equal(activationScheduler,  TaskScheduler.Current);  // "TaskScheduler.Current #0"

                Log(2, "Starting wrapper Task");
                wrapper = Task.Factory.StartNew(() =>
                {
                    Log(3, "Inside wrapper Task Id=" + Task.CurrentId);
                    Assert.Equal(activationScheduler,  TaskScheduler.Current);  // "TaskScheduler.Current #1"

                    Log(4, "Wrapper Task " + Task.CurrentId + " creating AC chain");
                    Task promise1 = Task.Factory.StartNew(() =>
                    {
                        Log(5, "#1 Inside AC Task Id=" + Task.CurrentId);
                        Assert.Equal(activationScheduler,  TaskScheduler.Current);  // "TaskScheduler.Current #1"
                        SubProcess1(1);
                    });
                    Task promise2 = promise1.ContinueWith((_) =>
                    {
                        Log(6, "#2 Inside AC Task Id=" + Task.CurrentId);
                        Assert.Equal(activationScheduler,  TaskScheduler.Current);  // "TaskScheduler.Current #2"
                        SubProcess1(2);
                    });
                    finalPromise = promise2.ContinueWith((_) =>
                    {
                        Log(7, "#3 Inside final AC Task Id=" + Task.CurrentId);
                        Assert.Equal(activationScheduler,  TaskScheduler.Current);  // "TaskScheduler.Current #3"
                        SubProcess1(3);
                        result.SetResult(true);
                    });
                    finalPromise.Ignore();

                    Log(8, "Wrapper Task Id=" + Task.CurrentId + " sleeping");
                    Thread.Sleep(TimeSpan.FromSeconds(1));

                    Log(9, "Wrapper Task Id=" + Task.CurrentId + " finished");
                });

                Log(10, "Outer ClosureWorkItem Task Id=" + Task.CurrentId + " sleeping");
                Thread.Sleep(TimeSpan.FromSeconds(1));
                Log(11, "Outer ClosureWorkItem Task Id=" + Task.CurrentId + " awake");

                Log(12, "Finished Outer TaskWorkItem Task Id=" + wrapper.Id);
                this.mainDone = true;
            });

            Log(13, "Waiting for ClosureWorkItem to spawn wrapper Task");
            for (int i = 0; i < 5 * WaitFactor; i++)
            {
                if (wrapper != null) break;
                Thread.Sleep(TimeSpan.FromSeconds(1).Multiply(WaitFactor));
            }
            Assert.NotNull(wrapper); // Wrapper Task was not created

            Log(14, "Waiting for wrapper Task Id=" + wrapper.Id + " to complete");
            bool finished = wrapper.Wait(TimeSpan.FromSeconds(4 * WaitFactor));
            Log(15, "Done waiting for wrapper Task Id=" + wrapper.Id + " Finished=" + finished);
            if (!finished) throw new TimeoutException();
            Assert.False(wrapper.IsFaulted, "Wrapper Task faulted: " + wrapper.Exception);
            Assert.True(wrapper.IsCompleted, "Wrapper Task should be completed");

            Log(16, "Waiting for TaskWorkItem to complete");
            for (int i = 0; i < 15 * WaitFactor; i++)
            {
                if (this.mainDone) break;
                Thread.Sleep(1000 * WaitFactor);
            }
            Log(17, "Done waiting for TaskWorkItem to complete MainDone=" + this.mainDone);
            Assert.True(this.mainDone, "Main Task should be completed");
            Assert.NotNull(finalPromise); // AC chain not created

            Log(18, "Waiting for final AC promise to complete");
            finalPromise.Wait(TimeSpan.FromSeconds(4 * WaitFactor));
            Log(19, "Done waiting for final promise");
            Assert.False(finalPromise.IsFaulted, "Final AC faulted: " + finalPromise.Exception);
            Assert.True(finalPromise.IsCompleted, "Final AC completed");
            Assert.True(result.Task.Result, "Timeout-1");

            Assert.NotEqual(0, this.stageNum1);  // "Work items did not get executed-1"
            Assert.Equal(3, this.stageNum1);  // "Work items executed out of order-1"
        }
        
        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public void Sched_AC_ContinueWith_1_Test()
        {
            UnitTestSchedulingContext context = new UnitTestSchedulingContext();
            var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            context.Scheduler = workItemGroup;

            var result = new TaskCompletionSource<bool>();
            int n = 0;
            // ReSharper disable AccessToModifiedClosure
            context.Scheduler.QueueAction(() =>
            {
                Task task1 = Task.Factory.StartNew(() => { this.output.WriteLine("===> 1a"); Thread.Sleep(OneSecond); n = n + 3; this.output.WriteLine("===> 1b"); });
                Task task2 = task1.ContinueWith((_) => { n = n * 5; this.output.WriteLine("===> 2"); });
                Task task3 = task2.ContinueWith((_) => { n = n / 5; this.output.WriteLine("===> 3"); });
                Task task4 = task3.ContinueWith((_) => { n = n - 2; this.output.WriteLine("===> 4"); result.SetResult(true); });
                task4.Ignore();
            });
            // ReSharper restore AccessToModifiedClosure

            Assert.True(result.Task.Wait(TwoSeconds));
            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(1,  n);  // "Work items executed out of order"
        }

        [Fact, TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
        public void Sched_Task_JoinAll()
        {
            var result = new TaskCompletionSource<bool>();
            int n = 0;
            Task<int>[] tasks = null;

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            UnitTestSchedulingContext context = new UnitTestSchedulingContext();
            var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            context.Scheduler = workItemGroup;

            // ReSharper disable AccessToModifiedClosure
            context.Scheduler.QueueAction(() =>
            {
                Task<int> task1 = Task<int>.Factory.StartNew(() => { this.output.WriteLine("===> 1a"); Thread.Sleep(OneSecond); n = n + 3; this.output.WriteLine("===> 1b"); return 1; });
                Task<int> task2 = Task<int>.Factory.StartNew(() => { this.output.WriteLine("===> 2a"); Thread.Sleep(OneSecond); n = n + 3; this.output.WriteLine("===> 2b"); return 2; });
                Task<int> task3 = Task<int>.Factory.StartNew(() => { this.output.WriteLine("===> 3a"); Thread.Sleep(OneSecond); n = n + 3; this.output.WriteLine("===> 3b"); return 3; });
                Task<int> task4 = Task<int>.Factory.StartNew(() => { this.output.WriteLine("===> 4a"); Thread.Sleep(OneSecond); n = n + 3; this.output.WriteLine("===> 4b"); return 4; });
                tasks = new Task<int>[] { task1, task2, task3, task4 };
                result.SetResult(true);
            });
            // ReSharper restore AccessToModifiedClosure
            Assert.True(result.Task.Wait(TwoSeconds)); // Wait for main (one that creates tasks) work item to finish.

            var promise = Task<int[]>.Factory.ContinueWhenAll(tasks, (res) => 
            {
                List<int> output = new List<int>();
                int taskNum = 1;
                foreach (var t in tasks)
                {
                    Assert.True(t.IsCompleted, "Sub-Task completed");
                    Assert.False(t.IsFaulted, "Sub-Task faulted: " + t.Exception);
                    var val = t.Result;
                    Assert.Equal(taskNum,  val);  // "Value returned by Task " + taskNum
                    output.Add(val);
                    taskNum++;
                }
                int[] results = output.ToArray();
                return results;
            });
            bool ok = promise.Wait(TimeSpan.FromSeconds(8));
            if (!ok) throw new TimeoutException();

            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(12,  n);  // "Not all work items executed"
            long ms = stopwatch.ElapsedMilliseconds;
            Assert.True(4000 <= ms && ms <= 5000, "Wait time out of range, expected between 4000 and 5000 milliseconds, was " + ms);
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public void Sched_AC_ContinueWith_2_OrleansSched()
        {
            var context = new UnitTestSchedulingContext();
            var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            context.Scheduler = workItemGroup;

            var result1 = new TaskCompletionSource<bool>();
            var result2 = new TaskCompletionSource<bool>();
            bool failed1 = false;
            bool failed2 = false;

            Task task1 = Task.Factory.StartNew(
                () => { this.output.WriteLine("===> 1a"); Thread.Sleep(OneSecond); throw new ArgumentException(); },
                CancellationToken.None,
                TaskCreationOptions.RunContinuationsAsynchronously,
                workItemGroup.TaskScheduler);
            
            Task task2 = task1.ContinueWith((Task t) =>
            {
                if (!t.IsFaulted) this.output.WriteLine("===> 2");
                else
                {
                    this.output.WriteLine("===> 3");
                    failed1 = true; 
                    result1.SetResult(true);
                }
            },
            workItemGroup.TaskScheduler);
            Task task3 = task1.ContinueWith((Task t) =>
            {
                if (!t.IsFaulted) this.output.WriteLine("===> 4");
                else
                {
                    this.output.WriteLine("===> 5");
                    failed2 = true; 
                    result2.SetResult(true);
                }
            },
            workItemGroup.TaskScheduler);
    
            task1.Ignore();
            task2.Ignore();
            task3.Ignore();
            Assert.True(result1.Task.Wait(TwoSeconds), "First ContinueWith did not fire.");
            Assert.True(result2.Task.Wait(TwoSeconds), "Second ContinueWith did not fire.");
            Assert.True(failed1);  // "First ContinueWith did not fire error handler."
            Assert.True(failed2);  // "Second ContinueWith did not fire error handler."
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public void Sched_Task_SchedulingContext()
        {
            var context = new UnitTestSchedulingContext();
            var workItemGroup = SchedulingHelper.CreateWorkItemGroupForTesting(context, loggerFactory);
            context.Scheduler = workItemGroup;

            var result = new TaskCompletionSource<bool>();
            Task endOfChain = null;
            int n = 0;

            Task wrapper = new Task(() =>
            {
                CheckRuntimeContext(context);

                // ReSharper disable AccessToModifiedClosure
                Task task1 = Task.Factory.StartNew(() =>
                {
                    this.output.WriteLine("===> 1a ");
                    CheckRuntimeContext(context);
                    Thread.Sleep(1000); 
                    n = n + 3;
                    this.output.WriteLine("===> 1b");
                    CheckRuntimeContext(context);
                });
                Task task2 = task1.ContinueWith(task =>
                {
                    this.output.WriteLine("===> 2");
                    CheckRuntimeContext(context);
                    n = n * 5; 
                });
                Task task3 = task2.ContinueWith(task => 
                {
                    this.output.WriteLine("===> 3");
                    n = n / 5;
                    CheckRuntimeContext(context);
                });
                Task task4 = task3.ContinueWith(task => 
                {
                    this.output.WriteLine("===> 4"); 
                    n = n - 2;
                    result.SetResult(true);
                    CheckRuntimeContext(context);
                });
                // ReSharper restore AccessToModifiedClosure
                endOfChain = task4.ContinueWith(task =>
                {
                    this.output.WriteLine("Done Faulted={0}", task.IsFaulted);
                    CheckRuntimeContext(context);
                    Assert.False(task.IsFaulted, "Faulted with Exception=" + task.Exception);
                });
            });
            wrapper.Start(workItemGroup.TaskScheduler);
            bool ok = wrapper.Wait(TimeSpan.FromSeconds(1));
            if (!ok) throw new TimeoutException();

            Assert.False(wrapper.IsFaulted, "Wrapper Task Faulted with Exception=" + wrapper.Exception);
            Assert.True(wrapper.IsCompleted, "Wrapper Task completed");
            bool finished = result.Task.Wait(TimeSpan.FromSeconds(2));
            Assert.NotNull(endOfChain); // End of chain Task created successfully
            Assert.False(endOfChain.IsFaulted, "Task chain Faulted with Exception=" + endOfChain.Exception);
            Assert.True(finished, "Wrapper Task completed ok");
            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(1,  n);  // "Work items executed out of order"
        }

        private void Log(int level, string what)
        {
            this.output.WriteLine("#{0} - {1} -- Thread={2} Worker={3} TaskScheduler.Current={4}",
                level, what,
                Thread.CurrentThread.ManagedThreadId,
                Thread.CurrentThread.Name,
                TaskScheduler.Current);
        }

        private static void CheckRuntimeContext(IGrainContext context)
        {
            Assert.NotNull(RuntimeContext.Current); // Runtime context should not be null
            Assert.NotNull(RuntimeContext.Current); // Activation context should not be null
            Assert.Equal(context, RuntimeContext.Current);  // "Activation context"
        }
    }
}
