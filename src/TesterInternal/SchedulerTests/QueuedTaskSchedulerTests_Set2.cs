using System;
using System.Collections.Generic;
using System.Runtime.Remoting.Messaging;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Schedulers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime;
using UnitTests.TesterInternal;

// ReSharper disable ConvertToConstant.Local

namespace UnitTests.SchedulerTests
{
    [TestClass]
    public class QueuedTaskSchedulerTests_Set2
    {
        public static bool Verbose { get; set; }

        private const int numTasks = 1000000;

        [TestInitialize]
        public void TestSetup()
        {
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TestCleanup]
        public void TestTeardown()
        {
            SynchronizationContext.SetSynchronizationContext(null);
            TraceLogger.SetTraceLevelOverrides(new List<Tuple<string, Severity>>()); // Reset Log level overrides
        }


        [TestMethod, TestCategory("Scheduler"), TestCategory("Tasks"), TestCategory("TaskScheduler")]
        public void Task_Basic()
        {
            string testName = "Task_Basic";
            DoBaseTestRun(testName, numTasks);
        }

        [TestMethod, TestCategory("Scheduler"), TestCategory("Tasks"), TestCategory("TaskScheduler")]
        public void Task_MultiSyncContext()
        {
            string testName = "Task_MultiSyncContext";

            var baseline = DoBaseTestRun(testName + "-Baseline", numTasks);

            var tasks = new List<Task>(numTasks);

            SynchronizationContext[] syncContexts = new SynchronizationContext[numTasks];
            for (int i = 0; i < numTasks; i++)
            {
                syncContexts[i] = new AsyncTestContext();
            }

            QueuedTaskSchedulerTests_Set1.TimeRun(1, baseline, testName, () =>
            {
                for (int i = 0; i < numTasks; i++)
                {
                    Task t = CreateTask(i);
                    SynchronizationContext.SetSynchronizationContext(syncContexts[i]);
                    t.Start();
                    tasks.Add(t);
                }

                Task.WaitAll(tasks.ToArray());
            });

            foreach (Task t in tasks)
            {
                Assert.IsTrue(t.IsCompleted, "Task is completed");
                Assert.IsFalse(t.IsFaulted, "Task did not fault");
                Assert.IsNull(t.Exception, "Task did not return an Exception");
            }
        }

        [TestMethod, TestCategory("Scheduler"), TestCategory("Tasks"), TestCategory("TaskScheduler")]
        public void Task_OneSyncContext()
        {
            string testName = "Task_OneSyncContext";

            var baseline = DoBaseTestRun(testName + "-Baseline", numTasks);

            var syncContext = new AsyncTestContext();
            SynchronizationContext.SetSynchronizationContext(syncContext);

            var tasks = new List<Task>(numTasks);

            QueuedTaskSchedulerTests_Set1.TimeRun(1, baseline, testName, () =>
            {
                for (int i = 0; i < numTasks; i++)
                {
                    Task t = CreateTask(i);
                    t.Start();
                    tasks.Add(t);
                }

                Task.WaitAll(tasks.ToArray());
            });

            foreach (Task t in tasks)
            {
                Assert.IsTrue(t.IsCompleted, "Task is completed");
                Assert.IsFalse(t.IsFaulted, "Task did not fault");
                Assert.IsNull(t.Exception, "Task did not return an Exception");
            }
        }

        [TestMethod, TestCategory("Scheduler"), TestCategory("Tasks"), TestCategory("TaskScheduler")]
        public void Task_OneContextMultiScheduler()
        {
            string testName = "Task_OneContextMultiScheduler";

            var baseline = DoBaseTestRun(testName + "-Baseline", numTasks);

            var tasks = new List<Task>(numTasks);

            var syncContext = new AsyncTestContext();
            SynchronizationContext.SetSynchronizationContext(syncContext);

            TaskScheduler[] schedulers = new TaskScheduler[numTasks];
            for (int i = 0; i < numTasks; i++)
            {
                schedulers[i] = GetTaskScheduler();
            }

            QueuedTaskSchedulerTests_Set1.TimeRun(1, baseline, testName, () =>
            {
                for (int i = 0; i < numTasks; i++)
                {
                    Task t = CreateTask(i);
                    t.Start(schedulers[i]);
                    tasks.Add(t);
                }

                Task.WaitAll(tasks.ToArray());
            });

            foreach (Task t in tasks)
            {
                Assert.IsTrue(t.IsCompleted, "Task is completed");
                Assert.IsFalse(t.IsFaulted, "Task did not fault");
                Assert.IsNull(t.Exception, "Task did not return an Exception");
            }
        }

        [TestMethod, TestCategory("Scheduler"), TestCategory("Tasks"), TestCategory("TaskScheduler")]
        public void Task_OneContextOneScheduler()
        {
            string testName = "Task_OneContextOneScheduler";

            var baseline = DoBaseTestRun(testName + "-Baseline", numTasks);

            var tasks = new List<Task>(numTasks);

            var syncContext = new AsyncTestContext();
            SynchronizationContext.SetSynchronizationContext(syncContext);

            var taskScheduler = GetTaskScheduler();

            QueuedTaskSchedulerTests_Set1.TimeRun(1, baseline, testName, () =>
            {
                for (int i = 0; i < numTasks; i++)
                {
                    Task t = CreateTask(i);
                    t.Start(taskScheduler);
                    tasks.Add(t);
                }

                Task.WaitAll(tasks.ToArray());
            });

            foreach (Task t in tasks)
            {
                Assert.IsTrue(t.IsCompleted, "Task is completed");
                Assert.IsFalse(t.IsFaulted, "Task did not fault");
                Assert.IsNull(t.Exception, "Task did not return an Exception");
            }
        }

        [TestMethod, TestCategory("Scheduler"), TestCategory("Tasks"), TestCategory("TaskScheduler")]
        public void Task_MultiTaskScheduler()
        {
            string testName = "Task_MultiTaskScheduler";

            var baseline = DoBaseTestRun(testName + "-Baseline", numTasks);

            var tasks = new List<Task>(numTasks);

            TaskScheduler[] schedulers = new TaskScheduler[numTasks];
            for (int i = 0; i < numTasks; i++)
            {
                schedulers[i] = GetTaskScheduler();
            }

            QueuedTaskSchedulerTests_Set1.TimeRun(1, baseline, testName, () =>
            {
                for (int i = 0; i < numTasks; i++)
                {
                    Task t = CreateTask(i);
                    t.Start(schedulers[i]);
                    tasks.Add(t);
                }

                Task.WaitAll(tasks.ToArray());
            });

            foreach (Task t in tasks)
            {
                Assert.IsTrue(t.IsCompleted, "Task is completed");
                Assert.IsFalse(t.IsFaulted, "Task did not fault");
                Assert.IsNull(t.Exception, "Task did not return an Exception");
            }
        }

        [TestMethod, TestCategory("Scheduler"), TestCategory("Tasks"), TestCategory("TaskScheduler")]
        public void Task_OneTaskScheduler()
        {
            string testName = "Task_OneTaskScheduler";

            var baseline = DoBaseTestRun(testName + "-Baseline", numTasks);

            var tasks = new List<Task>(numTasks);

            var taskScheduler = GetTaskScheduler();

            QueuedTaskSchedulerTests_Set1.TimeRun(1, baseline, testName, () =>
            {
                for (int i = 0; i < numTasks; i++)
                {
                    Task t = CreateTask(i);
                    t.Start(taskScheduler);
                    tasks.Add(t);
                }

                Task.WaitAll(tasks.ToArray());
            });

            foreach (Task t in tasks)
            {
                Assert.IsTrue(t.IsCompleted, "Task is completed");
                Assert.IsFalse(t.IsFaulted, "Task did not fault");
                Assert.IsNull(t.Exception, "Task did not return an Exception");
            }
        }

        [TestMethod, TestCategory("Scheduler"), TestCategory("Tasks"), TestCategory("TaskScheduler")]
        public void Task_MasterTaskScheduler()
        {
            string testName = "Task_MasterTaskScheduler";

            var baseline = DoBaseTestRun(testName + "-Baseline", numTasks);

            var tasks = new List<Task>(numTasks);

            var masterScheduler = GetTaskScheduler();
            TaskScheduler[] schedulers = new TaskScheduler[numTasks];
            for (int i = 0; i < numTasks; i++)
            {
                schedulers[i] = new TaskSchedulerWrapper(masterScheduler);
            }

            QueuedTaskSchedulerTests_Set1.TimeRun(1, baseline, testName, () =>
            {
                for (int i = 0; i < numTasks; i++)
                {
                    Task t = CreateTask(i);
                    t.Start(schedulers[i]);
                    tasks.Add(t);
                }

                Task.WaitAll(tasks.ToArray());
            });

            foreach (Task t in tasks)
            {
                Assert.IsTrue(t.IsCompleted, "Task is completed");
                Assert.IsFalse(t.IsFaulted, "Task did not fault");
                Assert.IsNull(t.Exception, "Task did not return an Exception");
            }
        }

        [TestMethod, TestCategory("Scheduler"), TestCategory("Tasks"), TestCategory("TaskScheduler")]
        public void Task_OneSchedulerMultiContexts()
        {
            string testName = "Task_OneSchedulerMultiContexts";

            var baseline = DoBaseTestRun(testName + "-Baseline", numTasks);

            var tasks = new List<Task>(numTasks);

            var taskScheduler = GetTaskScheduler();

            SynchronizationContext[] syncContexts = new SynchronizationContext[numTasks];
            for (int i = 0; i < numTasks; i++)
            {
                syncContexts[i] = new AsyncTestContext();
            }

            QueuedTaskSchedulerTests_Set1.TimeRun(1, baseline, testName, () =>
            {
                for (int i = 0; i < numTasks; i++)
                {
                    Task t = CreateTask(i);
                    SynchronizationContext.SetSynchronizationContext(syncContexts[i]);
                    t.Start(taskScheduler);
                    tasks.Add(t);
                }

                Task.WaitAll(tasks.ToArray());
            });

            foreach (Task t in tasks)
            {
                Assert.IsTrue(t.IsCompleted, "Task is completed");
                Assert.IsFalse(t.IsFaulted, "Task did not fault");
                Assert.IsNull(t.Exception, "Task did not return an Exception");
            }
        }

        [TestMethod, TestCategory("Scheduler"), TestCategory("Tasks"), TestCategory("TaskScheduler")]
        public void Task_MultiSchedulerMultiContexts()
        {
            string testName = "Task_MultiSchedulerMultiContexts";

            var baseline = DoBaseTestRun(testName + "-Baseline", numTasks);

            var tasks = new List<Task>(numTasks);

            SynchronizationContext[] syncContexts = new SynchronizationContext[numTasks];
            for (int i = 0; i < numTasks; i++)
            {
                syncContexts[i] = new AsyncTestContext();
            }
            TaskScheduler[] schedulers = new TaskScheduler[numTasks];
            for (int i = 0; i < numTasks; i++)
            {
                schedulers[i] = GetTaskScheduler();
            }

            QueuedTaskSchedulerTests_Set1.TimeRun(1, baseline, testName, () =>
            {
                for (int i = 0; i < numTasks; i++)
                {
                    Task t = CreateTask(i);
                    SynchronizationContext.SetSynchronizationContext(syncContexts[i]);
                    t.Start(schedulers[i]);
                    tasks.Add(t);
                }

                Task.WaitAll(tasks.ToArray());
            });

            foreach (Task t in tasks)
            {
                Assert.IsTrue(t.IsCompleted, "Task is completed");
                Assert.IsFalse(t.IsFaulted, "Task did not fault");
                Assert.IsNull(t.Exception, "Task did not return an Exception");
            }
        }

        [TestMethod, TestCategory("Scheduler"), TestCategory("Tasks"), TestCategory("TaskScheduler")]
        public void Task_OrleansTaskScheduler()
        {
            string testName = "Task_OrleansTaskScheduler";

            var baseline = DoBaseTestRun(testName + "-Baseline", numTasks);

            var tasks = new List<Task>(numTasks);

            UnitTestSchedulingContext rootContext = new UnitTestSchedulingContext();

            TaskScheduler taskScheduler = TestInternalHelper.InitializeSchedulerForTesting(rootContext);

            QueuedTaskSchedulerTests_Set1.TimeRun(1, baseline, testName, () =>
            {
                for (int i = 0; i < numTasks; i++)
                {
                    string context = i.ToString();
                    Task t = CreateTask(i, context);
                    t.Start(taskScheduler);
                    tasks.Add(t);
                }

                Task.WaitAll(tasks.ToArray());
            });

            foreach (Task t in tasks)
            {
                Assert.IsTrue(t.IsCompleted, "Task is completed");
                Assert.IsFalse(t.IsFaulted, "Task did not fault");
                Assert.IsNull(t.Exception, "Task did not return an Exception");
            }
        }

        [TestMethod, TestCategory("Scheduler"), TestCategory("Tasks"), TestCategory("TaskScheduler")]
        public void Sched_Task_OnCompletion()
        {
            var asyncContext = new AsyncTestContext();
            int numActions = 0;

            Action action = () =>
            {
                Console.WriteLine("Action");
                numActions++;
            };

            SynchronizationContext.SetSynchronizationContext(asyncContext);

            Task t = Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
            t.Wait();

            Assert.AreEqual(1, asyncContext.NumOperationsStarted, "AsynContext.OperationStarted");
            Assert.AreEqual(1, asyncContext.NumOperationsCompleted, "AsynContext.OperationCompleted");
            Assert.AreEqual(1, numActions, "Actions");
        }

        [TestMethod, TestCategory("Scheduler"), TestCategory("Tasks"), TestCategory("TaskScheduler"), TestCategory("RequestContext")]
        public void Task_LogicalCallContext()
        {
            string testName = "Task_LogicalCallContext";
            string name = "Foo";
            string val = "Bar";

            var baseline = DoBaseTestRun(testName + "-Baseline", numTasks);

            CallContext.LogicalSetData(name, val);
            Assert.AreEqual(val, CallContext.LogicalGetData(name), "LogicalGetData outside Task");

            var tasks = new List<Task>(numTasks);

            QueuedTaskSchedulerTests_Set1.TimeRun(1, baseline, testName, () =>
            {
                for (int i = 0; i < numTasks; i++)
                {
                    int id = i;
                    Task t = new Task(() =>
                    {
                        if (Verbose) Console.WriteLine("Task: " + id);
                        Assert.AreEqual(val, CallContext.LogicalGetData(name), "LogicalGetData inside Task " + id);
                    });
                    t.Start();
                    tasks.Add(t);
                }

                Task.WaitAll(tasks.ToArray());
            });

            foreach (Task t in tasks)
            {
                Assert.IsTrue(t.IsCompleted, "Task is completed");
                Assert.IsFalse(t.IsFaulted, "Task did not fault");
                Assert.IsNull(t.Exception, "Task did not return an Exception");
            }
        }

        private static Task CreateTask(int taskId, object state = null)
        {
            Task t;
            if (state != null)
            {
                t = new Task(o =>
                {
                    int id = taskId;
                    if (Verbose) Console.WriteLine("Task: " + id);
                }, state);
            }
            else
            {
                t = new Task(() =>
                {
                    int id = taskId;
                    if (Verbose) Console.WriteLine("Task: " + id);
                });
            }
            return t;
        }

        private static TaskScheduler GetTaskScheduler()
        {
            //var excl = new ConcurrentExclusiveInterleave();
            //return (exclusive) ? excl.ExclusiveTaskScheduler : excl.ConcurrentTaskScheduler;

            //return new OrderedTaskScheduler();

            return new QueuedTaskScheduler();
        }

        private static TimeSpan DoBaseTestRun(string runName, int numberOfTasks, TaskScheduler taskScheduler = null)
        {
            Console.WriteLine("NumTasks=" + numberOfTasks);

            var baseline = QueuedTaskSchedulerTests_Set1.TimeRun(1, TimeSpan.Zero, runName, () =>
            {
                var taskList = new List<Task>(numberOfTasks);

                for (int i = 0; i < numberOfTasks; i++)
                {
                    Task t = CreateTask(i);
                    if (taskScheduler == null)
                    {
                        t.Start();
                    }
                    else
                    {
                        t.Start(taskScheduler);
                    }
                    taskList.Add(t);
                }

                Task.WaitAll(taskList.ToArray());
            });
            return baseline;
        }
    }


    internal class AsyncTestContext : SynchronizationContext
    {
        private static long idCounter;
        private readonly long myId;

        internal int NumOperationsStarted;
        internal int NumOperationsCompleted;

        public AsyncTestContext()
        {
            myId = Interlocked.Increment(ref idCounter);
        }

        public override SynchronizationContext CreateCopy()
        {
            Console.WriteLine(OpName("CreateCopy"));
            return base.CreateCopy();
        }

        public override void OperationStarted()
        {
            Console.WriteLine(OpName("OperationStarted"));
            Interlocked.Increment(ref NumOperationsStarted);
            base.OperationStarted();
        }

        public override void OperationCompleted()
        {
            Console.WriteLine(OpName("OperationCompleted"));
            Interlocked.Increment(ref NumOperationsCompleted);
            base.OperationCompleted();
        }

        public override void Post(SendOrPostCallback d, object state)
        {
            Console.WriteLine(OpName("Post") + " " + string.Format("state={0} delegate={1}", state, d));
            OperationStarted();
            base.Post(d, state);
            OperationCompleted();
        }

        public override void Send(SendOrPostCallback d, object state)
        {
            Console.WriteLine(OpName("Send") + " " + string.Format("state={0} delegate={1}", state, d));
            OperationStarted();
            base.Send(d, state);
            OperationCompleted();
        }

        public override int Wait(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout)
        {
            string opName = waitAll ? "WaitAll" : "WaitAny";
            Console.WriteLine(OpName(opName)
                + string.Format("[{0}] timeout={1}ms", waitHandles.Length, millisecondsTimeout));

            return base.Wait(waitHandles, waitAll, millisecondsTimeout);
        }

        private string OpName(string opName)
        {
            return string.Format("SynchronizationContext[{0}].{1}", myId, opName);
        }
    }
}

// ReSharper restore ConvertToConstant.Local
