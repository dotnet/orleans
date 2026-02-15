using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans;

#nullable enable

/// <summary>
/// Allows consumers to observe and participate in the client/silo's lifecycle.
/// </summary>
public interface IServiceLifecycle
{
    /// <summary>
    /// Triggered when the client/silo has fully started and is ready to accept traffic.
    /// </summary>
    IServiceLifecycleStage Started { get; }

    /// <summary>
    /// Triggered when the client/silo is beginning the shutdown process.
    /// </summary>
    IServiceLifecycleStage Stopping { get; }

    /// <summary>
    /// Triggered when the client/silo has completed its shutdown process.
    /// </summary>
    IServiceLifecycleStage Stopped { get; }
}

internal sealed class ServiceLifecycle<TLifecycleObservable>(ILogger<ServiceLifecycle<TLifecycleObservable>> logger) :
    IServiceLifecycle, ILifecycleParticipant<TLifecycleObservable>
        where TLifecycleObservable : ILifecycleObservable
{
    private readonly ServiceLifecycleNotificationStage _started = new(logger, "Started");
    private readonly ServiceLifecycleNotificationStage _stopping = new(logger, "Stopping");
    private readonly ServiceLifecycleNotificationStage _stopped = new(logger, "Stopped");

    public IServiceLifecycleStage Started => _started;
    public IServiceLifecycleStage Stopping => _stopping;
    public IServiceLifecycleStage Stopped => _stopped;

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
