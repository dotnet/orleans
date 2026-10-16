namespace Documentation.Diagnostics.DurableTasks;

public sealed class ShippingGrain : DurableGrain
{
    private const string CarrierChange = "carrier-api";

    private static async DurableTask Book(string orderId) => await Task.CompletedTask;

    private static async DurableTask Ship(string orderId) => await Task.CompletedTask;

#pragma warning disable ORLEANS0118
    // <orleans0118_violation>
    public async DurableTask ShipAsync(string orderId, string region, int newestVersion)
    {
        // A name which depends on the region records a separate decision for each region, and the range follows the
        // caller's argument rather than the code below it.
        var version = DurableTask.GetVersion($"carrier-api-{region}", 0, newestVersion);
        if (version >= 1)
        {
            await Book(orderId).WithId("book");
        }
        else
        {
            await Ship(orderId).WithId("ship");
        }
    }
    // </orleans0118_violation>
#pragma warning restore ORLEANS0118

    // <orleans0118_fix>
    public async DurableTask ShipWithCarrierAsync(string orderId)
    {
        // The name identifies the change, and the range states the branches which this code keeps.
        var version = DurableTask.GetVersion(CarrierChange, minSupported: 0, maxSupported: 1);
        if (version >= 1)
        {
            await Book(orderId).WithId("book");
        }
        else
        {
            await Ship(orderId).WithId("ship");
        }
    }
    // </orleans0118_fix>
}
