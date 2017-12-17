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
        private static readonly Action<WorkItemWrapper> NoOpFilter = _ => { };

        public WorkItemFilter(
            Action<WorkItemWrapper> onActionExecuting = null,
            Action<WorkItemWrapper> onActionExecuted = null,
            Func<Exception, WorkItemWrapper, bool> exceptionHandler = null)
            : this(onActionExecuting, onActionExecuted, exceptionHandler, null)
        {
        }

        private WorkItemFilter(
            Action<WorkItemWrapper> onActionExecuting,
            Action<WorkItemWrapper> onActionExecuted,
            Func<Exception, WorkItemWrapper, bool> exceptionHandler,
            WorkItemFilter next)
        {
            Next = next;
            OnActionExecuting = onActionExecuting ?? NoOpFilter;
            OnActionExecuted = onActionExecuted ?? NoOpFilter;
            ExceptionHandler = exceptionHandler ?? ((e, c) => true);
        }

        public WorkItemFilter Next { get; private set; }

        public Func<Exception, WorkItemWrapper, bool> ExceptionHandler { get; }

        public Action<WorkItemWrapper> OnActionExecuting { get; }

        public Action<WorkItemWrapper> OnActionExecuted { get; }

        public bool ExecuteWorkItem(WorkItemWrapper workItem)
        {
            return ExecuteWorkItem(workItem, Next);
        }

        public bool ExecuteWorkItem(WorkItemWrapper workItem, WorkItemFilter next)
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
        public ExceptionHandlerFilter(ILogger log) : base(
            exceptionHandler: (ex, workItem) =>
            {
                var tae = ex as ThreadAbortException;
                if (tae != null)
                {
                    if (tae.ExceptionState != null && tae.ExceptionState.Equals(true))
                    {
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

                return true;
            })
        {
        }
    }

    internal sealed class OuterExceptionHandlerFilter : WorkItemFilter
    {
        public OuterExceptionHandlerFilter(ILogger log) : base(
            exceptionHandler: (ex, workItem) =>
            {
                if (ex is ThreadAbortException)
                {
                    if (log.IsEnabled(LogLevel.Debug)) log.Debug("Received thread abort exception -- exiting. {0}", ex);
                    Thread.ResetAbort();
                }
                else
                {
                    log.Error(ErrorCode.Runtime_Error_100030, $"Worker thread caught an exception thrown from task {workItem.State}.", ex);
                }

                return false;
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
