using System;

namespace Orleans.Runtime.Scheduler
{
    internal abstract class WorkItemBase : IWorkItem
    {
        public abstract IGrainContext GrainContext { get; }

        public TimeSpan TimeSinceQueued 
        {
            get { return Utils.Since(TimeQueued); } 
        }

        public abstract string Name { get; }

        public abstract WorkItemType ItemType { get; }

        public DateTime TimeQueued { get; set; }

        public abstract void Execute();

        public bool IsSystemPriority => this.GrainContext is SystemTarget systemTarget && !systemTarget.IsLowPriority;

        public override string ToString()
        {
            return string.Format("[{0} WorkItem Name={1}, Ctx={2}]", 
                ItemType, 
                Name ?? string.Empty,
                GrainContext?.ToString() ?? "null"
            );
        }
    }
}

