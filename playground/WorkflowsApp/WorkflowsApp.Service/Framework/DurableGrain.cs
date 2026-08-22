using Orleans.DurableTasks;

namespace WorkflowsApp.Service;

public abstract class DurableGrain : Orleans.Journaling.DurableGrain
{
    protected DurableGrain()
    {
        _ = ServiceProvider.GetRequiredService<IDurableTaskGrainStorage>();
    }
}
