using System;

namespace Orleans.Runtime.Scheduler
{
    internal interface IWorkItem
    {
        string Name { get; }
        IGrainContext GrainContext { get; }
        void Execute();

        internal static readonly Action<object> ExecuteWorkItem = state => ((IWorkItem)state).Execute();
    }
}
