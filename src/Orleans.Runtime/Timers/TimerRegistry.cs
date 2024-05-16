using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Timers;

internal class TimerRegistry(ILoggerFactory loggerFactory, TimeProvider timeProvider) : ITimerRegistry
{
    private readonly ILogger _timerLogger = loggerFactory.CreateLogger<GrainTimer>();
    private readonly TimeProvider _timeProvider = timeProvider;

    public IGrainTimer RegisterTimer(IGrainContext grainContext, Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new GrainTimer(grainContext, _timerLogger, asyncCallback, state, _timeProvider);
        grainContext?.GetComponent<IGrainTimerRegistry>().OnTimerCreated(timer);
        timer.Change(dueTime, period);
        return timer;
    }
}
