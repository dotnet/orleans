using System;
using System.Threading;
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
            bool restoreFlow = false;
            try
            {
                if (!ExecutionContext.IsFlowSuppressed())
                {
                    ExecutionContext.SuppressFlow();
                    restoreFlow = true;
                }

                var task = new Task(action);
                task.Start(taskScheduler);
            }
            finally
            {
                if (restoreFlow)
                {
                    ExecutionContext.RestoreFlow();
                }
            }
        }

        public static void QueueAction(this TaskScheduler taskScheduler, Action<object> action, object state)
        {
            bool restoreFlow = false;
            try
            {
                if (!ExecutionContext.IsFlowSuppressed())
                {
                    ExecutionContext.SuppressFlow();
                    restoreFlow = true;
                }

                var task = new Task(action, state);
                task.Start(taskScheduler);
            }
            finally
            {
                if (restoreFlow)
                {
                    ExecutionContext.RestoreFlow();
                }
            }
        }

        public static void QueueWorkItem(this TaskScheduler taskScheduler, IWorkItem todo)
        {
            bool restoreFlow = false;
            try
            {
                if (!ExecutionContext.IsFlowSuppressed())
                {
                    ExecutionContext.SuppressFlow();
                    restoreFlow = true;
                }

                var workItemTask = new Task(TaskFunc, todo);
                workItemTask.Start(taskScheduler);
            }
            finally
            {
                if (restoreFlow)
                {
                    ExecutionContext.RestoreFlow();
                }
            }
        }
    }
}
