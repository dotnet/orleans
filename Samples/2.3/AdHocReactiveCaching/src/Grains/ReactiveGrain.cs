using System;
using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    public class ReactiveGrain : Grain
    {
        protected IDisposable RegisterReactivePoll(Func<object, Task> poll, object state)
        {
            return RegisterTimer(async _ =>
            {
                try
                {
                    await poll(_);
                }
                catch (TimeoutException)
                {
                }
            }, state, TimeSpan.Zero, TimeSpan.FromTicks(1));
        }

        protected IDisposable RegisterReactivePoll(Func<Task> poll)
        {
            return RegisterReactivePoll(_ => poll(), null);
        }
    }
}
