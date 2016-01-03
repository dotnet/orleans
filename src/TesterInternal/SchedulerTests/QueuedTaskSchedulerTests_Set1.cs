using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading.Tasks.Schedulers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace UnitTests.SchedulerTests
{
    [TestClass]
    public class QueuedTaskSchedulerTests_Set1
    {
        public bool Verbose { get; set; }

        [TestMethod, TestCategory("Scheduler"), TestCategory("Tasks"), TestCategory("TaskScheduler")]
        public void Task_GroupedMasterTaskScheduler()
        {
            const string testNameBase = "Task_GroupedMasterTaskScheduler";

            int[] testPoints = new[]
            {
                100,
                500,
                1000,
                2000,
                3000,
                4000,
                5000,
                10000,
                20000,
                30000,
                40000,
                50000,
                100000,
                500000
            };

            foreach (int numSchedulers in testPoints)
            {
                string testName = testNameBase + "-" + numSchedulers;

                int numTasks = numSchedulers * 10;
                
                Console.WriteLine(testName + " NumTasks=" + numTasks + " NumSchedulers=" + numSchedulers);

                // Run baseline test with single, Default scheduler
                var baseline = TimeRun(1, TimeSpan.Zero, testName + "-Baseline", () => RunTestLoop(numTasks, null));

                // Run test with many schedulers...

                // Pre-create schedulers
                var masterScheduler = new QueuedTaskScheduler();
                TaskScheduler[] schedulers = new TaskScheduler[numSchedulers];
                for (int i = 0; i < numSchedulers; i++)
                {
                    schedulers[i] = new TaskSchedulerWrapper(masterScheduler);
                }

                TimeRun(1, baseline, testName, () => RunTestLoop(numTasks, schedulers));
            }
        }

        private void RunTestLoop(int numberOfTasks, TaskScheduler[] schedulers)
        {
            var taskList = new List<Task>(numberOfTasks);

            for (int i = 0; i < numberOfTasks; i++)
            {
                int id = i; // capture
                Task t = new Task(() =>
                {
                    if (Verbose) Console.WriteLine("Task: " + id);
                });

                if (schedulers == null || schedulers.Length == 0)
                {
                    t.Start();
                }
                else
                {
                    var scheduler = schedulers[i % schedulers.Length];
                    t.Start(scheduler);
                }

                taskList.Add(t);
            }

            Task.WaitAll(taskList.ToArray());
        }

        public static TimeSpan TimeRun(int numIterations, TimeSpan baseline, string what, Action action)
        {
            var stopwatch = new Stopwatch();

            GC.Collect();
            long startMem = GC.GetTotalMemory(false);
            stopwatch.Start();

            action();

            stopwatch.Stop();
            TimeSpan duration = stopwatch.Elapsed;
            GC.Collect();
            long stopMem = GC.GetTotalMemory(false);
            long memUsed = stopMem - startMem;

            string timeDeltaStr = "";
            if (baseline > TimeSpan.Zero)
            {
                double delta = (duration - baseline).TotalMilliseconds / baseline.TotalMilliseconds;
                timeDeltaStr = String.Format("-- Duration change from baseline = {0:+0.0#;-0.0#}%", 100.0 * delta);
            }
            Console.WriteLine("Time for {0} doing {1} Duration = {2} {3} Memory used = {4:+#,##0;-#,##0}", Pluralizer(numIterations, "loop"), what, duration, timeDeltaStr, memUsed);
            return duration;
        }
        private static string Pluralizer(int num, string unit)
        {
            return num + " " + unit + ((num == 1) ? "" : "s");
        }
    }

    public class TaskSchedulerWrapper : TaskScheduler
    {
        private readonly TaskScheduler masterScheduler;

        public TaskSchedulerWrapper(TaskScheduler scheduler)
        {
            this.masterScheduler = scheduler;
        }

        protected override IEnumerable<Task> GetScheduledTasks()
        {
            return new Task[0];
        }

        protected override void QueueTask(Task task)
        {
            var t = new Task(() => this.TryExecuteTask(task));
            t.Start(masterScheduler);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            return false;
        }
    }
}
