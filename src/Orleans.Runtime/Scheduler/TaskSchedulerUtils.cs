using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Orleans.Runtime.Internal;

namespace Orleans.Runtime.Scheduler
{
    internal static class TaskSchedulerUtils
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void QueueAction(this ActivationTaskScheduler taskScheduler, Action action)
        {
            using var suppressExecutionContext = new ExecutionContextSuppressor();

            var task = new Task(action);
            task.Start(taskScheduler);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void QueueAction(this ActivationTaskScheduler taskScheduler, Action<object> action, object state)
        {
            using var suppressExecutionContext = new ExecutionContextSuppressor();

            var task = new Task(action, state);
            task.Start(taskScheduler);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void QueueWorkItem(this WorkItemGroup scheduler, IWorkItem workItem)
        {
            QueueAction(scheduler.TaskScheduler, IWorkItem.ExecuteWorkItem, workItem);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void QueueWorkItem(this IWorkItemScheduler scheduler, IWorkItem workItem)
        {
            scheduler.QueueAction(IWorkItem.ExecuteWorkItem, workItem);
        }
    }
}
