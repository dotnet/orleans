using System;
using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    public class ReactiveGrain : Grain
    {
        /// <summary>
        /// Registers a simple near-zero period timer that ignores <see cref="TimeoutException"/> exceptions.
        /// Does not make assumptions on how the reactive poll must work otherwise.
        /// </summary>
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
            },
            state,
            TimeSpan.Zero,
            TimeSpan.FromTicks(1));
        }

        /// <summary>
        /// Registers a simple near-zero period timer that ignores <see cref="TimeoutException"/> exceptions.
        /// Does make assumptions on how the reactive poll must work.
        /// 1) Calls <paramref name="initialize"/> to resolve the initialization value and then <paramref name="apply"/> to apply it.
        /// 2) Calls the <paramref name="poll"/> action until it times out or it returns a result.
        /// 3) If <paramref name="poll"/> fails with a <see cref="TimeoutException"/> then it ignores it and calls <paramref name="poll"/> again.
        /// 4) When <paramref name="poll"/> returns a value, calls <paramref name="validate"/> on the value.
        /// 5) If the <paramref name="validate"/> returns true, then calls <paramref name="apply"/>, otherwise calls <paramref name="failed"/>.
        /// 6) Goes back to 2).
        /// </summary>
        protected async Task<IDisposable> RegisterReactivePollAsync<T>(Func<Task<T>> initialize, Func<Task<T>> poll, Func<T, bool> validate, Func<T, Task> apply, Func<T, Task> failed = null)
        {
            if (initialize != null)
            {
                var init = await initialize();
                await apply(init);
            }

            return RegisterTimer(async _ =>
            {
                try
                {
                    var update = await poll();
                    if (validate(update))
                    {
                        await apply(update);
                    }
                    else
                    {
                        if (failed != null)
                        {
                            await failed(update);
                        }
                    }
                }
                catch (TimeoutException)
                {
                }
            },
            null,
            TimeSpan.Zero,
            TimeSpan.FromTicks(1));
        }

        protected IDisposable RegisterReactivePoll(Func<Task> poll)
        {
            return RegisterReactivePoll(_ => poll(), null);
        }
    }
}
