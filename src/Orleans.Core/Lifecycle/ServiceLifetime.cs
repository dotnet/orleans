using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans;

#nullable enable

/// <summary>
/// Allows consumers to observe and participate in the client/silo's lifecycle.
/// </summary>
public interface IServiceLifetime
{
    /// <summary>
    /// Triggered when the client/silo has fully started and is ready to accept traffic.
    /// </summary>
    IServiceLifetimeStage Started { get; }

    /// <summary>
    /// Triggered when the client/silo is beginning the shutdown process.
    /// </summary>
    IServiceLifetimeStage Stopping { get; }

    /// <summary>
    /// Triggered when the client/silo has completed its shutdown process.
    /// </summary>
    IServiceLifetimeStage Stopped { get; }
}

internal sealed class ServiceLifetime<TLifecycleObservable>(ILogger logger) :
    IServiceLifetime, ILifecycleParticipant<TLifecycleObservable>
        where TLifecycleObservable : ILifecycleObservable
{
    private readonly ServiceLifetimeStage _started = new(logger, "Started");
    private readonly ServiceLifetimeStage _stopping = new(logger, "Stopping");
    private readonly ServiceLifetimeStage _stopped = new(logger, "Stopped");

    public IServiceLifetimeStage Started => _started;
    public IServiceLifetimeStage Stopping => _stopping;
    public IServiceLifetimeStage Stopped => _stopped;

    public void Participate(TLifecycleObservable lifecycle)
    {
        lifecycle.Subscribe(
            observerName: nameof(Started),
            stage: ServiceLifecycleStage.Active,
            onStart: _started.NotifyCompleted,
            onStop: _ => Task.CompletedTask);

        lifecycle.Subscribe(
            observerName: nameof(Stopping),
            stage: ServiceLifecycleStage.Active,
            onStart: _ => Task.CompletedTask,
            onStop: _stopping.NotifyCompleted);

        lifecycle.Subscribe(
            observerName: nameof(Stopped),
            stage: ServiceLifecycleStage.RuntimeInitialize - 1,
            onStart: _ => Task.CompletedTask,
            onStop: _stopped.NotifyCompleted);
    }
}
