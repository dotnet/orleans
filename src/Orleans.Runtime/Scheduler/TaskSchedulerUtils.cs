using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime.Internal;

namespace Orleans.Runtime.Scheduler
{
    internal static class TaskSchedulerUtils
    {
        private static readonly Action<object> TaskFunc = RunWorkItemTask;
        private static readonly Action<object> ThreadPoolWorkItemTaskFunc = (state) => RunThreadPoolWorkItemTask((IThreadPoolWorkItem)state);

        private static void RunThreadPoolWorkItemTask(IThreadPoolWorkItem todo)
        {
            todo.Execute();
        }

        private static void RunWorkItemTask(object state)
        {
            var workItem = (RequestWorkItem)state;
            RuntimeContext.SetExecutionContext(workItem.GrainContext, out var originalContext);
            try
            {
                workItem.Execute();
            }
            finally
            {
                RuntimeContext.ResetExecutionContext(originalContext);
            }
        }

        public static void QueueAction(this ActivationTaskScheduler taskScheduler, Action action)
        {
            using var suppressExecutionContext = new ExecutionContextSuppressor(); 

            var task = new Task(action);
            task.Start(taskScheduler);
        }

        public static void QueueAction(this ActivationTaskScheduler taskScheduler, Action<object> action, object state)
        {
            using var suppressExecutionContext = new ExecutionContextSuppressor(); 

            var task = new Task(action, state);
            task.Start(taskScheduler);
        }

        public static void QueueRequestWorkItem(this ActivationTaskScheduler taskScheduler, RequestWorkItem requestWorkItem)
        {
            using var suppressExecutionContext = new ExecutionContextSuppressor(); 

            var workItemTask = new Task(TaskFunc, requestWorkItem);
            workItemTask.Start(taskScheduler);
        }

        public static void QueueThreadPoolWorkItem(this ActivationTaskScheduler taskScheduler, IThreadPoolWorkItem workItem)
        {
            using var suppressExecutionContext = new ExecutionContextSuppressor(); 

            var workItemTask = new Task(ThreadPoolWorkItemTaskFunc , workItem);
            workItemTask.Start(taskScheduler);
        }
    }
}
