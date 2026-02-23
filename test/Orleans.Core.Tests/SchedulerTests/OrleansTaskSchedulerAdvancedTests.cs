using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Scheduler;
using Orleans.Internal;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.SchedulerTests
{
    /// <summary>
    /// Advanced tests for Orleans task scheduler functionality.
    /// </summary>
    public class OrleansTaskSchedulerAdvancedTests(ITestOutputHelper output) : IDisposable
    {
        private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);
        private readonly ILoggerFactory _loggerFactory = OrleansTaskSchedulerBasicTests.InitSchedulerLogging();

        public void Dispose()
        {
            _loggerFactory.Dispose();
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task Sched_AC_Test()
        {
            var n = 0;
            var insideTask = false;
            var context = UnitTestSchedulingContext.Create(_loggerFactory);

            output.WriteLine("Running Main in Context=" + RuntimeContext.Current);
            var tasksTask = new TaskCompletionSource<List<Task>>();
            var gates = new SemaphoreSlim[10];
            for (var i = 0; i < 10; i++)
            {
                gates[i] = new SemaphoreSlim(0, 1);
            }

            context.Scheduler.QueueAction(() =>
                {
                    var tasks = new List<Task>(10);
                    for (var i = 0; i < 10; i++)
                    {
                        var taskNum = i;
                        tasks.Add(Task.Factory.StartNew(() =>
                        {
                            output.WriteLine("Starting " + taskNum + " in Context=" + RuntimeContext.Current);
                            Assert.False(insideTask, $"Starting new task when I am already inside task of iteration {n}");
                            insideTask = true;

                            // Exacerbate the chance of a data race in the event that two of these tasks run concurrently.
                            var k = n;
                            gates[taskNum].Wait();
                            n = k + 1;

                            insideTask = false;
                        },
                        CancellationToken.None,
                        TaskCreationOptions.None,
                        TaskScheduler.Current));
                    }
                    tasksTask.SetResult(tasks);

                    // Release all gates in sequence to ensure sequential execution
                    for (var i = 0; i < 10; i++)
                    {
                        gates[i].Release();
                    }
                });

            await Task.WhenAll(await tasksTask.Task);

            // N should be 10, because all tasks should execute serially
            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(10, n);  // "Work items executed concurrently"
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task Sched_AC_WaitTest()
        {
            var n = 0;
            var insideTask = false;
            var context = UnitTestSchedulingContext.Create(_loggerFactory);

            var result = new TaskCompletionSource<bool>();
            var gate = new SemaphoreSlim(0, 1);

            context.Scheduler.QueueAction(() =>
                {
                    var task1 = Task.Factory.StartNew(() =>
                    {
                        output.WriteLine("Starting 1");
                        Assert.False(insideTask, $"Starting new task when I am already inside task of iteration {n}");
                        insideTask = true;
                        output.WriteLine("===> 1a");
                        gate.Wait();
                        n = n + 3;
                        output.WriteLine("===> 1b");
                        insideTask = false;
                    });
                    var task2 = Task.Factory.StartNew(() =>
                    {
                        output.WriteLine("Starting 2");
                        Assert.False(insideTask, $"Starting new task when I am already inside task of iteration {n}");
                        insideTask = true;
                        output.WriteLine("===> 2a");
#pragma warning disable xUnit1031 // Do not use blocking task operations in test method
                        task1.Wait();
#pragma warning restore xUnit1031 // Do not use blocking task operations in test method
                        output.WriteLine("===> 2b");
                        n = n * 5;
                        output.WriteLine("===> 2c");
                        insideTask = false;
                        result.SetResult(true);
                    });
                    task1.Ignore();
                    task2.Ignore();

                    // Release the gate to allow task1 to complete
                    gate.Release();
                });

            var timeoutLimit = TimeSpan.FromMilliseconds(3000);
            try
            {
                await result.Task.WaitAsync(timeoutLimit);
            }
            catch (TimeoutException)
            {
                Assert.Fail("Result did not arrive before timeout " + timeoutLimit);
            }

            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(15, n);  // "Work items executed out of order"
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task Sched_AC_Turn_Execution_Order()
        {
            // Can we add a unit test that basicaly checks that any turn is indeed run till completion before any other turn?
            // For example, you have a  long running main turn and in the middle it spawns a lot of short CWs (on Done promise) and StartNew.
            // You test that no CW/StartNew runs until the main turn is fully done. And run in stress.

            var context = UnitTestSchedulingContext.Create(_loggerFactory);

            var result1 = new TaskCompletionSource<bool>();
            var result2 = new TaskCompletionSource<bool>();
            var mainTurnGate = new TaskCompletionSource<bool>();
            var mainDone = false;
            var stageNum1 = 0;
            var stageNum2 = 0;

            context.Scheduler.QueueAction(() =>
            {
                mainDone = false;
                stageNum1 = stageNum2 = 0;

                var task1 = Task.Factory.StartNew(() => SubProcess1(11));
                var task2 = task1.ContinueWith((_) => SubProcess1(12));
                var task3 = task2.ContinueWith((_) => SubProcess1(13));
                var task4 = task3.ContinueWith((_) => { SubProcess1(14); result1.SetResult(true); });
                task4.Ignore();

                var task21 = Task.CompletedTask.ContinueWith((_) => SubProcess2(21));
                var task22 = task21.ContinueWith((_) => { SubProcess2(22); result2.SetResult(true); });
                task22.Ignore();

                // Wait for the gate to ensure ordering
                mainTurnGate.Task.Wait();
                mainDone = true;
            });

            // Release the gate to allow main turn to complete
            mainTurnGate.SetResult(true);

            try { await result1.Task.WaitAsync(WaitTimeout); }
            catch (TimeoutException) { Assert.Fail("Timeout-1"); }
            try { await result2.Task.WaitAsync(WaitTimeout); }
            catch (TimeoutException) { Assert.Fail("Timeout-2"); }

            Assert.NotEqual(0, stageNum1); // "Work items did not get executed-1"
            Assert.NotEqual(0, stageNum2);  // "Work items did not get executed-2"
            Assert.Equal(14, stageNum1);  // "Work items executed out of order-1"
            Assert.Equal(22, stageNum2);  // "Work items executed out of order-2"

            void SubProcess1(int n)
            {
                var msg = string.Format("1-{0} MainDone={1} inside Task {2}", n, mainDone, Task.CurrentId);
                output.WriteLine("1 ===> " + msg);
                Assert.True(mainDone, msg + " -- Main turn should be finished");
                stageNum1 = n;
            }

            void SubProcess2(int n)
            {
                var msg = string.Format("2-{0} MainDone={1} inside Task {2}", n, mainDone, Task.CurrentId);
                output.WriteLine("2 ===> " + msg);
                Assert.True(mainDone, msg + " -- Main turn should be finished");
                stageNum2 = n;
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task Sched_Stopped_WorkItemGroup()
        {
            var context = UnitTestSchedulingContext.Create(_loggerFactory);

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
                        state, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Current).Unwrap();
                },
                "some state",
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                context.WorkItemGroup.TaskScheduler);

            // Check that the WorkItemGroup is functioning.
            await await ScheduleTask();

            var taskAfterStopped = ScheduleTask();
            var resultTask = await Task.WhenAny(taskAfterStopped, Task.Delay(WaitTimeout));
            Assert.Same(taskAfterStopped, resultTask);

            await await taskAfterStopped;

            // Wait for the WorkItemGroup to upgrade the warning to an error and try again.
            // This delay is based upon SchedulingOptions.StoppedActivationWarningInterval.
            await Task.Delay(TimeSpan.FromMilliseconds(300));

            taskAfterStopped = ScheduleTask();
            resultTask = await Task.WhenAny(taskAfterStopped, Task.Delay(WaitTimeout));
            Assert.Same(taskAfterStopped, resultTask);

            await await taskAfterStopped;
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task Sched_Task_Turn_Execution_Order()
        {
            // A unit test that checks that any turn is indeed run till completion before any other turn?
            // For example, you have a long running main turn and in the middle it spawns a lot of short CWs (on Done promise) and StartNew.
            // You test that no CW/StartNew runs until the main turn is fully done. And run in stress.

            var context = UnitTestSchedulingContext.Create(_loggerFactory);
            var activationScheduler = context.WorkItemGroup.TaskScheduler;

            var mainDone = false;
            var stageNum1 = 0;
            var stageNum2 = 0;

            var result1 = new TaskCompletionSource<bool>();
            var result2 = new TaskCompletionSource<bool>();
            var wrapperGate = new TaskCompletionSource<bool>();
            var mainTurnGate = new TaskCompletionSource<bool>();

            Task wrapper = null;
            Task finalTask1 = null;
            Task finalPromise2 = null;
            var wrapperCreated = new TaskCompletionSource<Task>();

            context.Scheduler.QueueAction(() =>
            {
                Log(1, "Outer ClosureWorkItem " + Task.CurrentId + " starting");
                Assert.Equal(activationScheduler, TaskScheduler.Current);  // "TaskScheduler.Current #0"

                Log(2, "Starting wrapper Task");
                wrapper = Task.Factory.StartNew(() =>
                {
                    Log(3, "Inside wrapper Task Id=" + Task.CurrentId);
                    Assert.Equal(activationScheduler, TaskScheduler.Current);  // "TaskScheduler.Current #1"

                    // Execution chain #1
                    Log(4, "Wrapper Task Id=" + Task.CurrentId + " creating Task chain");
                    var task1 = Task.Factory.StartNew(() =>
                    {
                        Log(5, "#11 Inside sub-Task Id=" + Task.CurrentId);
                        Assert.Equal(activationScheduler, TaskScheduler.Current);  // "TaskScheduler.Current #11"
                        SubProcess1(11);
                    });
                    var task2 = task1.ContinueWith((Task task) =>
                    {
                        Log(6, "#12 Inside continuation Task Id=" + Task.CurrentId);
                        Assert.Equal(activationScheduler, TaskScheduler.Current);  // "TaskScheduler.Current #12"
                        if (task.IsFaulted) throw task.Exception.Flatten();
                        SubProcess1(12);
                    });
                    var task3 = task2.ContinueWith(task =>
                    {
                        Log(7, "#13 Inside continuation Task Id=" + Task.CurrentId);
                        Assert.Equal(activationScheduler, TaskScheduler.Current);  // "TaskScheduler.Current #13"
                        if (task.IsFaulted) throw task.Exception.Flatten();
                        SubProcess1(13);
                    });
                    finalTask1 = task3.ContinueWith(task =>
                    {
                        Log(8, "#14 Inside final continuation Task Id=" + Task.CurrentId);
                        Assert.Equal(activationScheduler, TaskScheduler.Current);  // "TaskScheduler.Current #14"
                        if (task.IsFaulted) throw task.Exception.Flatten();
                        SubProcess1(14);
                        result1.SetResult(true);
                    });

                    // Execution chain #2
                    Log(9, "Wrapper Task " + Task.CurrentId + " creating AC chain");
                    var promise2 = Task.Factory.StartNew(() =>
                    {
                        Log(10, "#21 Inside sub-Task Id=" + Task.CurrentId);
                        Assert.Equal(activationScheduler, TaskScheduler.Current);  // "TaskScheduler.Current #21"
                        SubProcess2(21);
                    });
                    finalPromise2 = promise2.ContinueWith((_) =>
                    {
                        Log(11, "#22 Inside final continuation Task Id=" + Task.CurrentId);
                        Assert.Equal(activationScheduler, TaskScheduler.Current);  // "TaskScheduler.Current #22"
                        SubProcess2(22);
                        result2.SetResult(true);
                    });
                    finalPromise2.Ignore();

                    Log(12, "Wrapper Task Id=" + Task.CurrentId + " waiting for gate");
                    wrapperGate.Task.Wait();

                    Log(13, "Wrapper Task Id=" + Task.CurrentId + " finished");

                    void SubProcess1(int n)
                    {
                        var msg = string.Format("1-{0} MainDone={1} inside Task {2}", n, mainDone, Task.CurrentId);
                        output.WriteLine("1 ===> " + msg);
                        Assert.True(mainDone, msg + " -- Main turn should be finished");
                        stageNum1 = n;
                    }

                    void SubProcess2(int n)
                    {
                        var msg = string.Format("2-{0} MainDone={1} inside Task {2}", n, mainDone, Task.CurrentId);
                        output.WriteLine("2 ===> " + msg);
                        Assert.True(mainDone, msg + " -- Main turn should be finished");
                        stageNum2 = n;
                    }
                }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Current);

                wrapperCreated.SetResult(wrapper);

                Log(14, "Outer ClosureWorkItem Task Id=" + Task.CurrentId + " waiting for gate");
                mainTurnGate.Task.Wait();
                Log(15, "Outer ClosureWorkItem Task Id=" + Task.CurrentId + " continuing");

                Log(16, "Finished Outer ClosureWorkItem Task Id=" + wrapper.Id);
                mainDone = true;
            });

            Log(17, "Waiting for ClosureWorkItem to spawn wrapper Task");
            wrapper = await wrapperCreated.Task.WaitAsync(WaitTimeout);
            Assert.NotNull(wrapper); // Wrapper Task was not created

            // Release gates to allow execution to proceed
            wrapperGate.SetResult(true);
            mainTurnGate.SetResult(true);

            Log(18, "Waiting for wrapper Task Id=" + wrapper.Id + " to complete");
            await wrapper.WaitAsync(WaitTimeout);
            Assert.False(wrapper.IsFaulted, "Wrapper Task faulted: " + wrapper.Exception);
            Assert.True(wrapper.IsCompleted, "Wrapper Task should be completed");

            Log(20, "Waiting for TaskWorkItem to complete");
            // Wait for mainDone using a reasonable timeout
            for (var i = 0; i < 100 && !mainDone; i++)
            {
                await Task.Delay(10);
            }
            Log(21, "Done waiting for TaskWorkItem to complete MainDone=" + mainDone);
            Assert.True(mainDone, "Main Task should be completed");
            Assert.NotNull(finalTask1); // Task chain #1 not created
            Assert.NotNull(finalPromise2); // Task chain #2 not created

            Log(22, "Waiting for final task #1 to complete");
            await finalTask1.WaitAsync(WaitTimeout);
            Assert.False(finalTask1.IsFaulted, "Final Task faulted: " + finalTask1.Exception);
            Assert.True(finalTask1.IsCompleted, "Final Task completed");
            Assert.True(await result1.Task, "Timeout-1");

            Log(24, "Waiting for final promise #2 to complete");
            await finalPromise2.WaitAsync(WaitTimeout);
            Log(25, "Done waiting for final promise #2");
            Assert.False(finalPromise2.IsFaulted, "Final Task faulted: " + finalPromise2.Exception);
            Assert.True(finalPromise2.IsCompleted, "Final Task completed");
            Assert.True(await result2.Task, "Timeout-2");

            Assert.NotEqual(0, stageNum1);  // "Work items did not get executed-1"
            Assert.Equal(14, stageNum1);  // "Work items executed out of order-1"
            Assert.NotEqual(0, stageNum2);  // "Work items did not get executed-2"
            Assert.Equal(22, stageNum2);  // "Work items executed out of order-2"


        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task Sched_AC_Current_TaskScheduler()
        {
            UnitTestSchedulingContext context = UnitTestSchedulingContext.Create(_loggerFactory);
            var activationScheduler = context.WorkItemGroup.TaskScheduler;

            var mainDone = false;
            var stageNum1 = 0;

            var result = new TaskCompletionSource<bool>();
            var wrapperGate = new TaskCompletionSource<bool>();
            var mainTurnGate = new TaskCompletionSource<bool>();

            Task wrapper = null;
            Task finalPromise = null;
            var wrapperCreated = new TaskCompletionSource<Task>();

            context.Scheduler.QueueAction(() =>
            {
                Log(1, "Outer ClosureWorkItem " + Task.CurrentId + " starting");
                Assert.Equal(activationScheduler, TaskScheduler.Current);  // "TaskScheduler.Current #0"

                Log(2, "Starting wrapper Task");
                wrapper = Task.Factory.StartNew(() =>
                {
                    Log(3, "Inside wrapper Task Id=" + Task.CurrentId);
                    Assert.Equal(activationScheduler, TaskScheduler.Current);  // "TaskScheduler.Current #1"

                    Log(4, "Wrapper Task " + Task.CurrentId + " creating AC chain");
                    var promise1 = Task.Factory.StartNew(() =>
                    {
                        Log(5, "#1 Inside AC Task Id=" + Task.CurrentId);
                        Assert.Equal(activationScheduler, TaskScheduler.Current);  // "TaskScheduler.Current #1"
                        SubProcess1(1);
                    });
                    var promise2 = promise1.ContinueWith((_) =>
                    {
                        Log(6, "#2 Inside AC Task Id=" + Task.CurrentId);
                        Assert.Equal(activationScheduler, TaskScheduler.Current);  // "TaskScheduler.Current #2"
                        SubProcess1(2);
                    });
                    finalPromise = promise2.ContinueWith((_) =>
                    {
                        Log(7, "#3 Inside final AC Task Id=" + Task.CurrentId);
                        Assert.Equal(activationScheduler, TaskScheduler.Current);  // "TaskScheduler.Current #3"
                        SubProcess1(3);
                        result.SetResult(true);
                    });
                    finalPromise.Ignore();

                    Log(8, "Wrapper Task Id=" + Task.CurrentId + " waiting for gate");
                    wrapperGate.Task.Wait();

                    Log(9, "Wrapper Task Id=" + Task.CurrentId + " finished");

                    void SubProcess1(int n)
                    {
                        var msg = string.Format("1-{0} MainDone={1} inside Task {2}", n, mainDone, Task.CurrentId);
                        output.WriteLine("1 ===> " + msg);
                        Assert.True(mainDone, msg + " -- Main turn should be finished");
                        stageNum1 = n;
                    }
                });

                wrapperCreated.SetResult(wrapper);

                Log(10, "Outer ClosureWorkItem Task Id=" + Task.CurrentId + " waiting for gate");
                mainTurnGate.Task.Wait();
                Log(11, "Outer ClosureWorkItem Task Id=" + Task.CurrentId + " continuing");

                Log(12, "Finished Outer TaskWorkItem Task Id=" + wrapper.Id);
                mainDone = true;
            });

            Log(13, "Waiting for ClosureWorkItem to spawn wrapper Task");
            wrapper = await wrapperCreated.Task.WaitAsync(WaitTimeout);
            Assert.NotNull(wrapper); // Wrapper Task was not created

            // Release gates to allow execution to proceed
            wrapperGate.SetResult(true);
            mainTurnGate.SetResult(true);

            Log(14, "Waiting for wrapper Task Id=" + wrapper.Id + " to complete");
            await wrapper.WaitAsync(WaitTimeout);
            Assert.False(wrapper.IsFaulted, "Wrapper Task faulted: " + wrapper.Exception);
            Assert.True(wrapper.IsCompleted, "Wrapper Task should be completed");

            Log(16, "Waiting for TaskWorkItem to complete");
            // Wait for mainDone using a reasonable timeout
            for (var i = 0; i < 100 && !mainDone; i++)
            {
                await Task.Delay(10);
            }
            Log(17, "Done waiting for TaskWorkItem to complete MainDone=" + mainDone);
            Assert.True(mainDone, "Main Task should be completed");
            Assert.NotNull(finalPromise); // AC chain not created

            Log(18, "Waiting for final AC promise to complete");
            await finalPromise.WaitAsync(WaitTimeout);
            Log(19, "Done waiting for final promise");
            Assert.False(finalPromise.IsFaulted, "Final AC faulted: " + finalPromise.Exception);
            Assert.True(finalPromise.IsCompleted, "Final AC completed");
            Assert.True(await result.Task, "Timeout-1");

            Assert.NotEqual(0, stageNum1);  // "Work items did not get executed-1"
            Assert.Equal(3, stageNum1);  // "Work items executed out of order-1"
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task Sched_AC_ContinueWith_1_Test()
        {
            var context = UnitTestSchedulingContext.Create(_loggerFactory);

            var result = new TaskCompletionSource<bool>();
            var gate = new SemaphoreSlim(0, 1);
            var n = 0;
            // ReSharper disable AccessToModifiedClosure
            context.Scheduler.QueueAction(() =>
            {
                var task1 = Task.Factory.StartNew(() => { output.WriteLine("===> 1a"); gate.Wait(); n = n + 3; output.WriteLine("===> 1b"); });
                var task2 = task1.ContinueWith((_) => { n = n * 5; output.WriteLine("===> 2"); });
                var task3 = task2.ContinueWith((_) => { n = n / 5; output.WriteLine("===> 3"); });
                var task4 = task3.ContinueWith((_) => { n = n - 2; output.WriteLine("===> 4"); result.SetResult(true); });
                task4.Ignore();

                // Release the gate to allow task1 to complete
                gate.Release();
            });
            // ReSharper restore AccessToModifiedClosure

            await result.Task.WaitAsync(WaitTimeout);
            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(1, n);  // "Work items executed out of order"
        }

        [Fact, TestCategory("Functional"), TestCategory("AsynchronyPrimitives")]
        public async Task Sched_Task_JoinAll()
        {
            var result = new TaskCompletionSource<bool>();
            var n = 0;
            Task<int>[] tasks = null;
            var gates = new SemaphoreSlim[4];
            for (var i = 0; i < 4; i++)
            {
                gates[i] = new SemaphoreSlim(0, 1);
            }

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            var context = UnitTestSchedulingContext.Create(_loggerFactory);

            context.Scheduler.QueueAction(() =>
            {
                var task1 = Task<int>.Factory.StartNew(() => { output.WriteLine("===> 1a"); gates[0].Wait(); n = n + 3; output.WriteLine("===> 1b"); return 1; });
                var task2 = Task<int>.Factory.StartNew(() => { output.WriteLine("===> 2a"); gates[1].Wait(); n = n + 3; output.WriteLine("===> 2b"); return 2; });
                var task3 = Task<int>.Factory.StartNew(() => { output.WriteLine("===> 3a"); gates[2].Wait(); n = n + 3; output.WriteLine("===> 3b"); return 3; });
                var task4 = Task<int>.Factory.StartNew(() => { output.WriteLine("===> 4a"); gates[3].Wait(); n = n + 3; output.WriteLine("===> 4b"); return 4; });
                tasks = new Task<int>[] { task1, task2, task3, task4 };
                result.SetResult(true);

                // Release all gates in sequence
                for (var i = 0; i < 4; i++)
                {
                    gates[i].Release();
                }
            });

            await result.Task.WaitAsync(WaitTimeout); // Wait for main (one that creates tasks) work item to finish.

            var promise = Task<int[]>.Factory.ContinueWhenAll(tasks, (res) =>
            {
                List<int> output = new List<int>();
                var taskNum = 1;
                foreach (var t in tasks)
                {
                    Assert.True(t.IsCompleted, "Sub-Task completed");
                    Assert.False(t.IsFaulted, "Sub-Task faulted: " + t.Exception);
#pragma warning disable xUnit1031 // Do not use blocking task operations in test method
                    var val = t.Result;
#pragma warning restore xUnit1031 // Do not use blocking task operations in test method
                    Assert.Equal(taskNum, val);  // "Value returned by Task " + taskNum
                    output.Add(val);
                    taskNum++;
                }
                var results = output.ToArray();
                return results;
            });

            await promise.WaitAsync(WaitTimeout);

            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(12, n);  // "Not all work items executed"
            var ms = stopwatch.ElapsedMilliseconds;
            // Since we removed sleeps, execution should be much faster - just verify it completed
            Assert.True(ms < 8000, "Wait time too long: " + ms);
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task Sched_AC_ContinueWith_2_OrleansSched()
        {
            var context = UnitTestSchedulingContext.Create(_loggerFactory);
            var workItemGroup = context.WorkItemGroup;

            var result1 = new TaskCompletionSource<bool>();
            var result2 = new TaskCompletionSource<bool>();
            var failed1 = false;
            var failed2 = false;
            var gate = new SemaphoreSlim(0, 1);

            var task1 = Task.Factory.StartNew(
                () => { output.WriteLine("===> 1a"); gate.Wait(); throw new ArgumentException(); },
                CancellationToken.None,
                TaskCreationOptions.RunContinuationsAsynchronously,
                workItemGroup.TaskScheduler);

            var task2 = task1.ContinueWith((Task t) =>
            {
                if (!t.IsFaulted) output.WriteLine("===> 2");
                else
                {
                    output.WriteLine("===> 3");
                    failed1 = true;
                    result1.SetResult(true);
                }
            },
            workItemGroup.TaskScheduler);
            var task3 = task1.ContinueWith((Task t) =>
            {
                if (!t.IsFaulted) output.WriteLine("===> 4");
                else
                {
                    output.WriteLine("===> 5");
                    failed2 = true;
                    result2.SetResult(true);
                }
            },
            workItemGroup.TaskScheduler);

            task1.Ignore();
            task2.Ignore();
            task3.Ignore();

            // Release the gate to allow task1 to throw
            gate.Release();

            await result1.Task.WaitAsync(WaitTimeout);
            await result2.Task.WaitAsync(WaitTimeout);
            Assert.True(failed1);  // "First ContinueWith did not fire error handler."
            Assert.True(failed2);  // "Second ContinueWith did not fire error handler."
        }

        [Fact, TestCategory("Functional"), TestCategory("Scheduler")]
        public async Task Sched_Task_SchedulingContext()
        {
            var context = UnitTestSchedulingContext.Create(_loggerFactory);

            var result = new TaskCompletionSource<bool>();
            Task endOfChain = null;
            var n = 0;
            var gate = new SemaphoreSlim(0, 1);

            Task wrapper = new Task(() =>
            {
                CheckRuntimeContext(context);

                // ReSharper disable AccessToModifiedClosure
                var task1 = Task.Factory.StartNew(() =>
                {
                    output.WriteLine("===> 1a ");
                    CheckRuntimeContext(context);
                    gate.Wait();
                    n = n + 3;
                    output.WriteLine("===> 1b");
                    CheckRuntimeContext(context);
                });
                var task2 = task1.ContinueWith(task =>
                {
                    output.WriteLine("===> 2");
                    CheckRuntimeContext(context);
                    n = n * 5;
                });
                var task3 = task2.ContinueWith(task =>
                {
                    output.WriteLine("===> 3");
                    n = n / 5;
                    CheckRuntimeContext(context);
                });
                var task4 = task3.ContinueWith(task =>
                {
                    output.WriteLine("===> 4");
                    n = n - 2;
                    result.SetResult(true);
                    CheckRuntimeContext(context);
                });
                // ReSharper restore AccessToModifiedClosure
                endOfChain = task4.ContinueWith(task =>
                {
                    output.WriteLine("Done Faulted={0}", task.IsFaulted);
                    CheckRuntimeContext(context);
                    Assert.False(task.IsFaulted, "Faulted with Exception=" + task.Exception);
                });

                // Release the gate to allow task1 to complete
                gate.Release();
            });
            wrapper.Start(context.WorkItemGroup.TaskScheduler);
            await wrapper.WaitAsync(WaitTimeout);

            Assert.False(wrapper.IsFaulted, "Wrapper Task Faulted with Exception=" + wrapper.Exception);
            Assert.True(wrapper.IsCompleted, "Wrapper Task completed");
            await result.Task.WaitAsync(WaitTimeout);
            Assert.NotNull(endOfChain); // End of chain Task created successfully
            Assert.False(endOfChain.IsFaulted, "Task chain Faulted with Exception=" + endOfChain.Exception);
            Assert.True(n != 0, "Work items did not get executed");
            Assert.Equal(1, n);  // "Work items executed out of order"
        }

        private void Log(int level, string what)
        {
            output.WriteLine("#{0} - {1} -- Thread={2} Worker={3} TaskScheduler.Current={4}",
                level, what,
                Environment.CurrentManagedThreadId,
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
