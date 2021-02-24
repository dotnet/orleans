using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.Scheduler
{
    internal static class TaskSchedulerUtils
    {
        private static readonly Action<object> TaskFunc = (state) => RunWorkItemTask((IWorkItem)state);

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
            var task = new Task(action);
            task.Start(taskScheduler);
        }

        public static void QueueAction(this TaskScheduler taskScheduler, Action<object> action, object state)
        {
            var task = new Task(action, state);
            task.Start(taskScheduler);
        }

        public static void QueueWorkItem(this TaskScheduler taskScheduler, IWorkItem todo)
        {
            var workItemTask = new Task(TaskFunc, todo);
            workItemTask.Start(taskScheduler);
        }
    }
}
