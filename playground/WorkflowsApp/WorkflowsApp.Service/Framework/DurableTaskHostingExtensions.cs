namespace WorkflowsApp.Service;

public static class DurableTaskHostingExtensions
{
    public static ISiloBuilder AddVolatileDurableTaskStorage(this ISiloBuilder siloBuilder)
        => Orleans.Journaling.DurableTasks.DurableTaskHostingExtensions.AddVolatileDurableTaskStorage(siloBuilder);

    public static ISiloBuilder AddJournaledDurableTaskStorage(this ISiloBuilder siloBuilder)
        => Orleans.Journaling.DurableTasks.DurableTaskHostingExtensions.AddJournaledDurableTaskStorage(siloBuilder);
}
