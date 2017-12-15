using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    /// <summary>
    /// Allows clear definition of action behavior wrappers
    /// </summary>
    internal abstract class WorkItemFilter
    {
        private static readonly Action<QueueWorkItemCallback> NoOpFilter = _ => { };

        public WorkItemFilter(
            Action<QueueWorkItemCallback> onActionExecuting = null,
            Action<QueueWorkItemCallback> onActionExecuted = null,
            Func<Exception, QueueWorkItemCallback, bool> exceptionHandler = null)
            : this(onActionExecuting, onActionExecuted, exceptionHandler, null)
        {
        }

        private WorkItemFilter(
            Action<QueueWorkItemCallback> onActionExecuting,
            Action<QueueWorkItemCallback> onActionExecuted,
            Func<Exception, QueueWorkItemCallback, bool> exceptionHandler,
            WorkItemFilter next)
        {
            Next = next;
            OnActionExecuting = onActionExecuting ?? NoOpFilter;
            OnActionExecuted = onActionExecuted ?? NoOpFilter;
            ExceptionHandler = exceptionHandler ?? ((e, c) => true);
        }

        public WorkItemFilter Next { get; private set; }

        public Func<Exception, QueueWorkItemCallback, bool> ExceptionHandler { get; }

        public Action<QueueWorkItemCallback> OnActionExecuting { get; }

        public Action<QueueWorkItemCallback> OnActionExecuted { get; }

        public bool ExecuteWorkItem(QueueWorkItemCallback workItem)
        {
            return ExecuteWorkItem(workItem, Next);
        }

        public bool ExecuteWorkItem(QueueWorkItemCallback workItem, WorkItemFilter next)
        {
            try
            {
                OnActionExecuting(workItem);
                if (next == null)
                {
                    workItem.Execute();
                    return true;
                }
                else
                {
                    return next.ExecuteWorkItem(workItem, next.Next);
                }
            }
            catch (Exception ex)
            {
                if (!ExceptionHandler(ex, workItem))
                {
                    throw;
                }
            }
            finally
            {
                OnActionExecuted(workItem);
            }

            return true;
        }

        public static WorkItemFilter[] CreateChain(IEnumerable<Func<WorkItemFilter>> workItemsFactories)
        {
            WorkItemFilter first = null;
            var workItemFilters = new List<WorkItemFilter>();
            foreach (var fact in workItemsFactories.Reverse())
            {
                var workItem = fact();
                workItem.Next = first;
                workItemFilters.Add(workItem);
                first = workItem;
            }

            workItemFilters.Reverse();
            return workItemFilters.ToArray();
        }
    }


    internal sealed class ExceptionHandlerFilter : WorkItemFilter
    {
        public ExceptionHandlerFilter(ILogger log, bool continueExecution) : base(
            exceptionHandler: (ex, workItem) =>
            {
                var tae = ex as ThreadAbortException;
                if (tae != null)
                {
                    if (tae.ExceptionState != null && tae.ExceptionState.Equals(true))
                    {
                        // todo: not needed?
                        Thread.ResetAbort();
                    }
                    else
                    {
                        log.Error(ErrorCode.Runtime_Error_100029,
                            "Caught thread abort exception, allowing it to propagate outwards", ex);
                    }
                }
                else
                {
                    log.Error(ErrorCode.Runtime_Error_100030, $"Worker thread caught an exception thrown from task {workItem.State}.", ex);
                }

                return continueExecution;
            })
        {
        }
    }

    internal sealed class WorkerThreadStatisticsFilter : WorkItemFilter
    {
        public WorkerThreadStatisticsFilter() : base(
            onActionExecuted: workItem =>
            {
#if TRACK_DETAILED_STATS // todo
                if (todo.ItemType != WorkItemType.WorkItemGroup)
                {
                    if (StatisticsCollector.CollectTurnsStats)
                    {
                        //SchedulerStatisticsGroup.OnTurnExecutionEnd(CurrentStateTime.Elapsed);
                        SchedulerStatisticsGroup.OnTurnExecutionEnd(Utils.Since(CurrentStateStarted));
                    }
                    if (StatisticsCollector.CollectThreadTimeTrackingStats)
                    {
                        threadTracking.IncrementNumberOfProcessed();
                    }
                    CurrentWorkItem = null;
                }
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    threadTracking.OnStopProcessing();
                }
#endif
            })
        {
        }
    }
}
