using System.Threading;

namespace Orleans.Runtime.Scheduler
{
    internal interface IWorkItem : IThreadPoolWorkItem
    {
        string Name { get; }
        IGrainContext GrainContext { get; }
    }
}
