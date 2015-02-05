using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace Orleans.TestFramework
{
    /// <summary>
    /// Utility functions for task
    /// </summary>
    public class TaskHelper
    {
        private static bool isInitialized = false;
        /// <summary>
        /// Initialize UnobservedTaskException
        /// </summary>
        public static void Init()
        {
            unobservedExceptionList = new List<Exception>();
            if (!isInitialized)
            {
                TaskScheduler.UnobservedTaskException += TaskSchedulerOnUnobservedTaskException;
                isInitialized = true;
            }
        }

        /// <summary>
        /// List of unobserved exceptions
        /// </summary>
        private static List<Exception> unobservedExceptionList;
        /// <summary>
        /// Waits until all tasks completed or one throws an exception
        /// </summary>
        /// <param name="tasks">tasks to wait on</param>
        public static void WaitUntilAllCompletedOrOneFailed(Task[] tasks)
        {
            WaitUntilAllCompletedOrOneFailed(tasks.ToList());
        }
        /// <summary>
        /// Waits until all tasks completed or one throws an exception
        /// </summary>
        /// <param name="tasks">tasks to wait on</param>
        public static void WaitUntilAllCompletedOrOneFailed(List<Task> tasks)
        {
            var list = new List<Task>(); // don't use original list, we remove elements!
            list.AddRange(tasks);
            while (list.Count > 0)
            {
                int i = Task.WaitAny(list.ToArray());
                if (list[i].IsFaulted)
                {
                    foreach(Task t in list)
                    {
                        t.ContinueWith(LogTaskFailure);
                    }
                    throw new Exception("Task Failed:", list[i].Exception.InnerException);
                }
                else list.RemoveAt(i);
            }
        }

        /// <summary>
        /// Logs the task failure
        /// </summary>
        /// <param name="t"></param>
        public static void LogTaskFailuresAndThrow(Task[] tasks)
        {
            List<Exception> exList = new List<Exception>();
            foreach (Task t in tasks)
            {
                if (t.IsFaulted)
                {
                    exList.Add(t.Exception);
                }
                t.ContinueWith(LogTaskFailure);
            }
            if (exList.Count > 0)
                throw new AggregateException("Multiple Exceptions", exList.ToArray());
            
        }
        /// <summary>
        /// Logs the task failure
        /// </summary>
        /// <param name="t"></param>
        public static void LogTaskFailure(Task t)
        {
            try
            {
                if (t.IsFaulted) Log.WriteLine(SEV.ERROR, "TestResults", "Task threw an exception {0}", t.Exception);
            }
            catch
            {
            }
        }

        /// <summary>
        /// handler
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private static void TaskSchedulerOnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs args)
        {
            Log.WriteLine(SEV.ERROR, "TaskHelper", String.Format("Received UnobservedTaskException :{0}", args.Exception.ToString()));
            lock (unobservedExceptionList)
            {
                unobservedExceptionList.Add(args.Exception);
            }
            args.SetObserved();
        }

        /// <summary>
        /// 
        /// </summary>
        public static void CheckUnobservedExceptions()
        {
            GC.Collect();
            if (unobservedExceptionList.Count > 0)
            {
                throw new AggregateException(String.Format("Test Failed: Unobserved Exceptions: {0}",
                                                           Utils.IEnumerableToString(unobservedExceptionList)));
            }
        }

        public static Task ExecuteWithTimeout(Action action, TimeSpan timeout)
        {
            var resolver = new TaskCompletionSource<bool>();
            Timer timer = new Timer(obj =>
            {
                resolver.TrySetException(new TimeoutException(String.Format("The provided action did not finish in {0} time", timeout)));
            }, null, timeout, TimeSpan.FromMilliseconds(-1));

            Task.Factory.StartNew(() =>
                {
                    try
                    {
                        action();
                        resolver.TrySetResult(true);
                    }
                    catch (Exception exc)
                    {
                        resolver.TrySetException(exc);
                    }
                });       
            return resolver.Task;
        }
    }
}
