using System;
using System.Reflection;

namespace Orleans.Runtime.Scheduler
{
    internal class ClosureWorkItem : WorkItemBase
    {
        private readonly Action continuation;
        private readonly Func<string> nameGetter;

        public override string Name { get { return nameGetter==null ? "" : nameGetter(); } }

        public ClosureWorkItem(Action closure)
        {
            continuation = closure;
#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectGlobalShedulerStats)
            {
                SchedulerStatisticsGroup.OnClosureWorkItemsCreated();
            }
#endif
        }

        public ClosureWorkItem(Action closure, Func<string> getName)
        {
            continuation = closure;
            nameGetter = getName;
#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectGlobalShedulerStats)
            {
                SchedulerStatisticsGroup.OnClosureWorkItemsCreated();
            }
#endif
        }

        #region IWorkItem Members

        public override void Execute()
        {
#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectGlobalShedulerStats)
            {
                SchedulerStatisticsGroup.OnClosureWorkItemsExecuted();
            }
#endif
            continuation();
        }

        public override WorkItemType ItemType { get { return WorkItemType.Closure; } }

        #endregion

        public override string ToString()
        {
            var detailedName = string.Empty; 
            if (nameGetter == null) // if NameGetter != null, base.ToString() will print its name.
            {
                var continuationMethodInfo = continuation.GetMethodInfo();
                detailedName = string.Format(": {0}->{1}",
                   (continuation.Target == null) ? "" : continuation.Target.ToString(),
                   (continuationMethodInfo == null) ? "" : continuationMethodInfo.ToString());
            }
               

            return string.Format("{0}{1}", base.ToString(), detailedName);
        }
    }
}
