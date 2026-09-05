using System.Threading;
using System.Threading.Tasks;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Runtime.DurableTasks;

namespace Orleans.DurableTasks;

internal sealed class DurableTaskGrainParticipant(
    DurableTaskGrainRuntime runtime,
    IGrainContext grainContext) :
    IJournaledGrainParticipant,
    ILifecycleObserver,
    IActivationDeactivationParticipant
{
    public void Initialize()
    {
        ActivationDeactivationCoordinator.Register(grainContext, this);
        grainContext.ObservableLifecycle.Subscribe(
            nameof(DurableTaskGrainParticipant),
            GrainLifecycleStage.Activate + 1,
            this);
    }

    public Task OnStart(CancellationToken cancellationToken = default) =>
        runtime.ResumePendingTasksAsync(cancellationToken);

    public Task OnStop(CancellationToken cancellationToken = default) => runtime.StopAsync(cancellationToken);

    public void OnDeactivationRequested() => runtime.OnDeactivationRequested();

    public Task OnDeactivatingAsync(CancellationToken cancellationToken) => runtime.StopAsync(cancellationToken);
}
