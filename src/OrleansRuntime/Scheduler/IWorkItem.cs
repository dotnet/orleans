using System;


namespace Orleans.Runtime.Scheduler
{
    internal interface IWorkItem
    {
        string Name { get; }
        WorkItemType ItemType { get; }
        ISchedulingContext SchedulingContext { get; set; }
        TimeSpan TimeSinceQueued { get; }
        DateTime TimeQueued { get; set;  }
        bool IsSystemPriority { get; }
        void Execute();
    }
}
