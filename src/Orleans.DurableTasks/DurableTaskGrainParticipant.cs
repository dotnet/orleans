using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Runtime.DurableTasks;

namespace Orleans.DurableTasks;

internal sealed class DurableTaskGrainParticipant(
    DurableTaskGrainRuntime runtime,
    IGrainContext grainContext,
    IDurableJobHandlerRegistry jobHandlers,
    IJournaledStateManager stateManager) : IJournaledGrainParticipant, ILifecycleObserver
{
    public void Initialize()
    {
        jobHandlers.Register(DurableTaskMessageTransport.ResumeJobName, runtime);
        stateManager.RegisterObserver(runtime);
        grainContext.ObservableLifecycle.Subscribe(
            nameof(DurableTaskGrainParticipant),
            GrainLifecycleStage.Activate,
            this);
    }

    public Task OnStart(CancellationToken cancellationToken = default) =>
        runtime.ResumePendingTasksAsync(cancellationToken);

    public Task OnStop(CancellationToken cancellationToken = default) => runtime.StopAsync(cancellationToken);
}
