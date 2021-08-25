namespace Orleans.Runtime.Scheduler
{
    internal abstract class WorkItemBase : IWorkItem
    {
        public abstract IGrainContext GrainContext { get; }

        public abstract string Name { get; }

        public abstract void Execute();

        public override string ToString()
        {
            return string.Format("[{0} WorkItem Name={1}, Ctx={2}]", 
                GetType().Name, 
                Name ?? string.Empty,
                GrainContext?.ToString() ?? "null"
            );
        }
    }
}

