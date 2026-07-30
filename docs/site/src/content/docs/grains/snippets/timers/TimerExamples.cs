using Orleans.Runtime;

namespace Timers;

// Examples for grain timer usage documentation

// <grain_timer_example>
public class MyGrain : Grain, IMyGrain
{
    private IGrainTimer? _timer;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Create a timer that fires every 10 seconds, starting 5 seconds after activation
        // RegisterGrainTimer is an extension method on IGrainBase (which Grain implements)
        _timer = this.RegisterGrainTimer(
            static (state, ct) => state.DoWorkAsync(ct),
            this,
            new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.FromSeconds(5),
                Period = TimeSpan.FromSeconds(10),
                KeepAlive = true  // Prevent grain collection while timer is active
            });

        return Task.CompletedTask;
    }

    private Task DoWorkAsync(CancellationToken cancellationToken)
    {
        // Timer callback work
        return Task.CompletedTask;
    }
}
// </grain_timer_example>

// <migrate_after>
public class MyGrainAfter : Grain, IMyGrain
{
    private IGrainTimer? _timer;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Use this.RegisterGrainTimer() - an extension method on IGrainBase
        _timer = this.RegisterGrainTimer(
            static (state, ct) => state.DoWorkAsync(ct),
            this,
            new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.FromSeconds(5),
                Period = TimeSpan.FromSeconds(10),
                Interleave = true  // Set to true to match old behavior
            });
        
        return Task.CompletedTask;
    }

    private Task DoWorkAsync(CancellationToken cancellationToken)
    {
        // Timer work - check cancellationToken for graceful shutdown
        return Task.CompletedTask;
    }
}
// </migrate_after>

public interface IMyGrain : IGrainWithGuidKey
{
}
