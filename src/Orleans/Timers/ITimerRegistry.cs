using System;
using System.Threading.Tasks;

namespace Orleans.Timers
{
    public interface ITimerRegistry
    {
        IDisposable RegisterTimer(Grain grain, Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period);
    }
}