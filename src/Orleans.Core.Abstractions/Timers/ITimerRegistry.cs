using System;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Timers
{
    public interface ITimerRegistry
    {
        IGrainTimer RegisterTimer(Grain grain, Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period);
    }
}