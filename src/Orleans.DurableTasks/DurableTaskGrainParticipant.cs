using System.Threading;
using System.Threading.Tasks;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Runtime.DurableTasks;

namespace Orleans.DurableTasks;

internal sealed class DurableTaskGrainParticipant(
    DurableTaskGrainRuntime runtime,
    IGrainContext grainContext) : IJournaledGrainParticipant, ILifecycleObserver
{
    public void Initialize()
    {
        grainContext.ObservableLifecycle.Subscribe(
            nameof(DurableTaskGrainParticipant),
            GrainLifecycleStage.Activate,
            this);
    }

    public Task OnStart(CancellationToken cancellationToken = default) =>
        runtime.ResumePendingTasksAsync(cancellationToken);

    public Task OnStop(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
