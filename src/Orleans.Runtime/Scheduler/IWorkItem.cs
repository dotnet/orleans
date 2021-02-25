using System;
using System.Threading;

namespace Orleans.Runtime.Scheduler
{
    internal interface IWorkItem : IThreadPoolWorkItem
    {
        string Name { get; }
        WorkItemType ItemType { get; }
        IGrainContext GrainContext { get; }
        TimeSpan TimeSinceQueued { get; }
        DateTime TimeQueued { get; set;  }
        bool IsSystemPriority { get; }
    }
}
