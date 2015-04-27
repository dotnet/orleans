using System;
using System.Threading.Tasks;

namespace Orleans.Timers
{
    internal class TimerRegistry : ITimerRegistry
    {
        public IDisposable RegisterTimer(Grain grain, Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period)
        {
            return grain.Data.RegisterTimer(asyncCallback, state, dueTime, period);
        }
    }
}
