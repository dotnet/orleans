using System;
using System.Threading;

namespace Orleans.Runtime.Scheduler
{
    internal interface IWorkItem : IThreadPoolWorkItem
    {
        string Name { get; }
        IGrainContext GrainContext { get; }

        internal static readonly Action<object> ExecuteWorkItem = state => ((IWorkItem)state).Execute();
    }
}
