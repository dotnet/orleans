using System;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Timers
{
    public interface ITimerRegistry
    {
        IDisposable RegisterTimer(IGrainContext grainContext, Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period);
    }
}