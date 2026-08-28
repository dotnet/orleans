using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.DurableTasks.Protocol;

namespace Orleans.DurableTasks.Runtime;

internal sealed class DurableTaskGrainParticipant(
    DurableTaskGrainRuntime runtime,
    IGrainContext grainContext,
    IDurableJobHandlerRegistry jobHandlers,
    IJournaledStateManager stateManager) : IJournaledGrainParticipant, ILifecycleObserver
{
    public void Initialize()
    {
        jobHandlers.Register(runtime);
        try
        {
            stateManager.RegisterObserver(runtime);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidOperationException(
                "Durable Tasks requires IJournaledStateManager observer support through IJournaledStateManager.RegisterObserver.",
                exception);
        }

        grainContext.ObservableLifecycle.Subscribe(
            nameof(DurableTaskGrainParticipant),
            GrainLifecycleStage.Activate,
            this);
        runtime.InitializeForActivation();
    }

    public Task OnStart(CancellationToken cancellationToken = default) =>
        runtime.ResumePendingTasksAsync(cancellationToken);

    public Task OnStop(CancellationToken cancellationToken = default) => runtime.StopAsync(cancellationToken);
}
