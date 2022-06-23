using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime.Internal;

namespace Orleans.Runtime.Scheduler
{
    internal static class TaskSchedulerUtils
    {
        private static readonly Action<object> TaskFunc = (state) => RunWorkItemTask((IWorkItem)state);
        private static readonly Action<object> ThreadPoolWorkItemTaskFunc = (state) => RunThreadPoolWorkItemTask((IThreadPoolWorkItem)state);

        private static void RunThreadPoolWorkItemTask(IThreadPoolWorkItem todo)
        {
            todo.Execute();
        }

        private static void RunWorkItemTask(IWorkItem todo)
        {
            try
            {
                RuntimeContext.SetExecutionContext(todo.GrainContext);
                todo.Execute();
            }
            finally
            {
                RuntimeContext.ResetExecutionContext();
            }
        }

        public static void QueueAction(this TaskScheduler taskScheduler, Action action)
        {
            using var suppressExecutionContext = new ExecutionContextSuppressor(); 

            var task = new Task(action);
            task.Start(taskScheduler);
        }

        public static void QueueAction(this TaskScheduler taskScheduler, Action<object> action, object state)
        {
            using var suppressExecutionContext = new ExecutionContextSuppressor(); 

            var task = new Task(action, state);
            task.Start(taskScheduler);
        }

        public static void QueueWorkItem(this TaskScheduler taskScheduler, IWorkItem todo)
        {
            using var suppressExecutionContext = new ExecutionContextSuppressor(); 

            var workItemTask = new Task(TaskFunc, todo);
            workItemTask.Start(taskScheduler);
        }

        public static void QueueThreadPoolWorkItem(this TaskScheduler taskScheduler, IThreadPoolWorkItem workItem)
        {
            using var suppressExecutionContext = new ExecutionContextSuppressor(); 

            var workItemTask = new Task(ThreadPoolWorkItemTaskFunc , workItem);
            workItemTask.Start(taskScheduler);
        }
    }
}
