using Orleans;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Diagnostics;
using Orleans.Journaling;

#pragma warning disable ORLEANSEXP005

namespace Documentation.Grains.DurableTasks.Versioning;

// <durable_tasks_versioning_contracts>
public interface IFulfillmentGrain : IGrainWithStringKey
{
    DurableTask<string> FulfillAsync(string orderId);
}

public interface IWarehouseGrain : IGrainWithStringKey
{
    DurableTask<string> ShipAsync(string orderId);
}

public interface ICarrierGrain : IGrainWithStringKey
{
    DurableTask<string> BookAsync(string orderId);

    DurableTask NotifyAsync(string orderId, string trackingId);
}
// </durable_tasks_versioning_contracts>

public sealed class FulfillmentGrain : DurableGrain, IFulfillmentGrain
{
    private IWarehouseGrain Warehouse => GrainFactory.GetGrain<IWarehouseGrain>("main");

    private ICarrierGrain Carrier => GrainFactory.GetGrain<ICarrierGrain>("main");

    // <durable_tasks_versioning_branch>
    public async DurableTask<string> FulfillAsync(string orderId)
    {
        await Reserve(orderId).WithId("reserve");

        // Version 0 is the code which shipped through the warehouse. Version 1 books a carrier, and version 2 also
        // notifies the customer. Pending executions keep the version which they recorded.
        var shipping = DurableTask.GetVersion("carrier-booking", minSupported: 0, maxSupported: 2);
        if (shipping >= 1)
        {
            var trackingId = await Carrier.BookAsync(orderId).WithId("book");
            if (shipping >= 2)
            {
                await Carrier.NotifyAsync(orderId, trackingId).WithId("notify");
            }

            return trackingId;
        }

        return await Warehouse.ShipAsync(orderId).WithId("ship");
    }
    // </durable_tasks_versioning_branch>

    private static async DurableTask Reserve(string orderId) => await Task.CompletedTask;
}

public sealed class DescribedFulfillmentGrain : DurableGrain, IFulfillmentGrain
{
    private ICarrierGrain Carrier => GrainFactory.GetGrain<ICarrierGrain>("main");

    private IWarehouseGrain Warehouse => GrainFactory.GetGrain<IWarehouseGrain>("main");

    // <durable_tasks_versioning_descriptor>
    // One declaration states the change's name and the versions which this code supports.
    private static readonly DurableVersion CarrierBooking = new("carrier-booking", minSupported: 0, maxSupported: 2);

    public async DurableTask<string> FulfillAsync(string orderId)
    {
        if (!CarrierBooking.IsAtLeast(1))
        {
            return await Warehouse.ShipAsync(orderId).WithId("ship");
        }

        var trackingId = await Carrier.BookAsync(orderId).WithId("book");
        if (CarrierBooking.IsAtLeast(2))
        {
            await Carrier.NotifyAsync(orderId, trackingId).WithId("notify");
        }

        return trackingId;
    }
    // </durable_tasks_versioning_descriptor>
}

public sealed class RetiredFulfillmentGrain : DurableGrain, IFulfillmentGrain
{
    private ICarrierGrain Carrier => GrainFactory.GetGrain<ICarrierGrain>("main");

    // <durable_tasks_versioning_retired>
    public async DurableTask<string> FulfillAsync(string orderId)
    {
        await Reserve(orderId).WithId("reserve");

        // No pending execution follows version 0, so the warehouse branch is gone. An execution which recorded version
        // 0 stalls here until code which supports it replays it, or until an operator aborts it.
        var shipping = DurableTask.GetVersion("carrier-booking", minSupported: 1, maxSupported: 2);
        var trackingId = await Carrier.BookAsync(orderId).WithId("book");
        if (shipping >= 2)
        {
            await Carrier.NotifyAsync(orderId, trackingId).WithId("notify");
        }

        return trackingId;
    }
    // </durable_tasks_versioning_retired>

    private static async DurableTask Reserve(string orderId) => await Task.CompletedTask;
}

// <durable_tasks_versioning_monitor>
public sealed class VersionMonitor : IObserver<DurableTaskEvents.DurableTaskEvent>, IDisposable
{
    private readonly IDisposable _subscription;
    private readonly Dictionary<(string Name, int Version), long> _recorded = [];

    public VersionMonitor() => _subscription = DurableTaskEvents.AllEvents.Subscribe(this);

    public void OnNext(DurableTaskEvents.DurableTaskEvent value)
    {
        switch (value)
        {
            case DurableTaskEvents.VersionRecorded recorded:
                // Executions which record an old version show that it is still in use.
                lock (_recorded)
                {
                    _recorded[(recorded.Name, recorded.Version)] = _recorded.GetValueOrDefault((recorded.Name, recorded.Version)) + 1;
                }

                break;
            case DurableTaskEvents.ExecutionStalled stalled:
                // Reason is UnsupportedVersion or Diverged; the task stays pending until code which can replay it runs.
                Console.WriteLine($"{stalled.GrainContext.GrainId} {stalled.TaskId} stalled ({stalled.Reason}): {stalled.Exception.Message}");
                break;
        }
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void Dispose() => _subscription.Dispose();
}
// </durable_tasks_versioning_monitor>
