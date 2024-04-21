#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Timers;

internal class TimerRegistry(ILoggerFactory loggerFactory, TimeProvider timeProvider, MessageFactory messageFactory, ILocalSiloDetails localSiloDetails) : ITimerRegistry
{
    public ILogger TimerLogger { get; } = loggerFactory.CreateLogger<GrainTimer>();
    public TimeProvider TimeProvider { get; } = timeProvider;
    public MessageFactory MessageFactory { get; } = messageFactory;
    public ILocalSiloDetails LocalSiloDetails { get; } = localSiloDetails;

    public IDisposable RegisterTimer(IGrainContext grainContext, Func<object?, Task> callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(grainContext);
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new InterleavingGrainTimer(this, grainContext, callback, state);
        grainContext.GetComponent<IGrainTimerRegistry>()?.OnTimerCreated(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public IGrainTimer RegisterGrainTimer<T>(IGrainContext grainContext, Func<T, CancellationToken, Task> callback, T state, GrainTimerCreationOptions options)
    {
        ArgumentNullException.ThrowIfNull(grainContext);
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new GrainTimer<T>(this, grainContext, callback, state, options.Interleave, options.KeepAlive);
        grainContext.GetComponent<IGrainTimerRegistry>()?.OnTimerCreated(timer);
        timer.Change(options.DueTime, options.Period);
        return timer;
    }
}
